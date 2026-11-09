using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using CoreShift.Services.CoreAudio;
using static CoreShift.Services.CoreAudio.CoreAudio;
using static CoreShift.Services.CoreAudio.CoreAudioConstants;

namespace CoreShift.Services
{
    /// <summary>
    /// Enforces a global maximum volume limit for the default audio endpoint.
    /// Uses Windows Core Audio APIs (IAudioEndpointVolume, ISimpleAudioVolume) to clamp
    /// master volume and all active sessions to a user-defined maximum (1-100%).
    /// Follows the same patterns as GpuPowerService: JSON config, callbacks, polling fallback.
    /// </summary>
    public sealed class VolumeLimitService : IDisposable
    {
        private const string TaskName = "CoreShift Volume Limit";

        private static string StateDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CoreShift");
        private static string ConfigPath => Path.Combine(StateDir, "volume-limit.json");

        private sealed record VolumeConfig(float MaxVolumeScalar, bool Enabled);

        private const float DefaultMaxVolume = 0.5f; // 50%

        // COM objects (held alive for callbacks)
        private IMMDeviceEnumerator? _enumerator;
        private IMMDevice? _device;
        private IAudioEndpointVolume? _endpointVolume;
        private IAudioSessionManager2? _sessionManager;
        private IMMNotificationClient? _deviceNotification;
        private IAudioEndpointVolumeCallback? _volumeCallback;
        private IAudioSessionNotification? _sessionNotification;
        private Timer? _pollTimer;
        private bool _disposed;
        private bool _initialized;

        /// <summary>Current limit as scalar 0.01-1.0</summary>
        public float MaxVolumeScalar { get; private set; } = DefaultMaxVolume;

        /// <summary>Whether the limiter is active</summary>
        public bool Enabled { get; private set; } = true;

        /// <summary>Initializes the volume limiter from saved config and applies the limit.</summary>
        public Task<PowerResult> InitializeAsync() => Task.Run(() =>
        {
            if (_initialized) return PowerResult.Ok();

            var config = LoadConfig();
            MaxVolumeScalar = Math.Clamp(config.MaxVolumeScalar, 0.01f, 1.0f);
            Enabled = config.Enabled;

            if (!Enabled)
            {
                _initialized = true;
                return PowerResult.Ok();
            }

            return ApplyLimitInternal();
        });

        /// <summary>Sets a new maximum volume limit (0.01-1.0 scalar).</summary>
        public Task<PowerResult> SetLimitAsync(float maxVolumeScalar) => Task.Run(() =>
        {
            maxVolumeScalar = Math.Clamp(maxVolumeScalar, 0.01f, 1.0f);
            MaxVolumeScalar = maxVolumeScalar;

            var config = new VolumeConfig(MaxVolumeScalar, Enabled);
            WriteJson(ConfigPath, config);

            if (!Enabled) return PowerResult.Ok();

            return ApplyLimitInternal();
        });

        /// <summary>Enables or disables the volume limiter.</summary>
        public Task<PowerResult> SetEnabledAsync(bool enabled) => Task.Run(() =>
        {
            Enabled = enabled;
            var config = new VolumeConfig(MaxVolumeScalar, Enabled);
            WriteJson(ConfigPath, config);

            if (!Enabled)
            {
                Cleanup();
                return PowerResult.Ok();
            }

            return ApplyLimitInternal();
        });

        private PowerResult ApplyLimitInternal()
        {
            Cleanup(); // Clean up any previous state

            try
            {
                // 1. Create device enumerator using Activator.CreateInstance (avoids ComImport issues)
                var enumeratorType = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"));
                if (enumeratorType == null) return PowerResult.Fail("Failed to get MMDeviceEnumerator type");
                var enumeratorInstance = Activator.CreateInstance(enumeratorType);
                if (enumeratorInstance == null) return PowerResult.Fail("Failed to create MMDeviceEnumerator");
                _enumerator = (IMMDeviceEnumerator)enumeratorInstance;

                // 2. Get default render endpoint (speakers/headphones)
                int hr = _enumerator.GetDefaultAudioEndpoint(
                    (int)EDataFlow.eRender,
                    (int)ERole.eConsole,
                    out _device);
                if (hr != S_OK)
                    return PowerResult.Fail($"GetDefaultAudioEndpoint failed: 0x{hr:X}");

                // 3. Activate IAudioEndpointVolume for master volume control
                var iidEndpointVolume = IID_IAudioEndpointVolume;
                hr = _device.Activate(ref iidEndpointVolume, CLSCTX_ALL, IntPtr.Zero, out var volPtr);
                if (hr != S_OK)
                    return PowerResult.Fail($"Activate IAudioEndpointVolume failed: 0x{hr:X}");
                _endpointVolume = (IAudioEndpointVolume)Marshal.GetObjectForIUnknown(volPtr);
                Marshal.Release(volPtr);

                // 4. Activate IAudioSessionManager2 for per-session control
                var iidSessionManager = IID_IAudioSessionManager2;
                hr = _device.Activate(ref iidSessionManager, CLSCTX_ALL, IntPtr.Zero, out var mgrPtr);
                if (hr != S_OK)
                    return PowerResult.Fail($"Activate IAudioSessionManager2 failed: 0x{hr:X}");
                _sessionManager = (IAudioSessionManager2)Marshal.GetObjectForIUnknown(mgrPtr);
                Marshal.Release(mgrPtr);

                // 5. Apply initial limit to master volume
                ClampMasterVolume();

                // 5b. Apply limit to all existing sessions
                ClampAllSessions();

                // 6. Register for master volume change notifications
                _volumeCallback = new VolumeCallback(this);
                hr = _endpointVolume.RegisterControlChangeNotify(_volumeCallback);
                if (hr != S_OK)
                    return PowerResult.Fail($"RegisterControlChangeNotify failed: 0x{hr:X}");

                // 7. Register for new session notifications
                _sessionNotification = new SessionNotification(this);
                hr = _sessionManager.RegisterSessionNotification(_sessionNotification);
                if (hr != S_OK)
                    return PowerResult.Fail($"RegisterSessionNotification failed: 0x{hr:X}");

                // 8. Register for device changes (default endpoint switch)
                _deviceNotification = new DeviceNotification(this);
                hr = _enumerator.RegisterEndpointNotificationCallback(_deviceNotification);
                if (hr != S_OK)
                    return PowerResult.Fail($"RegisterEndpointNotificationCallback failed: 0x{hr:X}");

                // 9. Start periodic polling timer (fallback for missed notifications)
                _pollTimer = new Timer(_ => ClampAllSessions(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));

                _initialized = true;
                return PowerResult.Ok();
            }
            catch (Exception ex)
            {
                Cleanup();
                return PowerResult.Fail($"Volume limiter init failed: {ex.Message}");
            }
        }

        private void ClampMasterVolume()
        {
            if (_endpointVolume == null) return;

            try
            {
                _endpointVolume.GetMasterVolumeLevelScalar(out float current);
                if (current > MaxVolumeScalar)
                {
                    var emptyGuid = Guid.Empty;
                    _endpointVolume.SetMasterVolumeLevelScalar(MaxVolumeScalar, ref emptyGuid);
                }
            }
            catch { /* Ignore transient COM errors */ }
        }

        private void ClampAllSessions()
        {
            if (_sessionManager == null) return;

            try
            {
                int hr = _sessionManager.GetSessionEnumerator(out IntPtr enumPtr);
                if (hr != S_OK || enumPtr == IntPtr.Zero) return;
                var enumerator = (IAudioSessionEnumerator)Marshal.GetObjectForIUnknown(enumPtr);
                Marshal.Release(enumPtr);

                enumerator.GetCount(out int count);

                for (int i = 0; i < count; i++)
                {
                    int hrSession = enumerator.GetSession(i, out IntPtr sessionPtr);
                    if (hrSession != S_OK || sessionPtr == IntPtr.Zero) continue;

                    // Get IAudioSessionControl2 first (for process ID)
                    var session2 = (IAudioSessionControl2)Marshal.GetObjectForIUnknown(sessionPtr);
                    Marshal.Release(sessionPtr);

                    try
                    {
                        session2.GetProcessId(out uint pid);

                        // Skip system sounds session (often has pid=0)
                        if (pid == 0) continue;

                        // Get ISimpleAudioVolume for volume control
                        var simpleVol = (ISimpleAudioVolume)session2;
                        simpleVol.GetMasterVolume(out float vol);
                        if (vol > MaxVolumeScalar)
                        {
                            var emptyGuid = Guid.Empty;
                            simpleVol.SetMasterVolume(MaxVolumeScalar, ref emptyGuid);
                        }
                    }
                    finally
                    {
                        if (session2 != null) Marshal.ReleaseComObject(session2);
                    }
                }
                if (enumerator != null) Marshal.ReleaseComObject(enumerator);
            }
            catch { /* Ignore transient COM errors during polling */ }
        }

        // Called from VolumeCallback when master volume changes
        internal void OnMasterVolumeChanged(float newVolume, bool muted)
        {
            if (newVolume > MaxVolumeScalar)
            {
                ClampMasterVolume();
            }
        }

        // Called from SessionNotification when new session created
        internal void OnSessionCreated(IAudioSessionControl newSession)
        {
            try
            {
                var session2 = (IAudioSessionControl2)newSession;
                int isSystem = session2.IsSystemSoundsSession();
                if (isSystem == S_OK) return;

                var simpleVol = (ISimpleAudioVolume)newSession;
                simpleVol.GetMasterVolume(out float vol);
                if (vol > MaxVolumeScalar)
                {
                    var emptyGuid = Guid.Empty;
                    simpleVol.SetMasterVolume(MaxVolumeScalar, ref emptyGuid);
                }
            }
            catch { }
        }

        // Called from DeviceNotification when default endpoint changes
        internal void OnDefaultDeviceChanged()
        {
            // Re-initialize on a thread pool thread to avoid blocking the callback
            Task.Run(() =>
            {
                try
                {
                    if (Enabled)
                    {
                        ApplyLimitInternal();
                    }
                }
                catch { }
            });
        }

        private void Cleanup()
        {
            _pollTimer?.Dispose();
            _pollTimer = null;

            if (_endpointVolume != null && _volumeCallback != null)
            {
                try { _endpointVolume.UnregisterControlChangeNotify(_volumeCallback); } catch { }
                _volumeCallback = null;
            }

            if (_sessionManager != null && _sessionNotification != null)
            {
                try { _sessionManager.UnregisterSessionNotification(_sessionNotification); } catch { }
                _sessionNotification = null;
            }

            if (_enumerator != null && _deviceNotification != null)
            {
                try { _enumerator.UnregisterEndpointNotificationCallback(_deviceNotification); } catch { }
                _deviceNotification = null;
            }

            // Release COM objects
            if (_endpointVolume != null) { Marshal.ReleaseComObject(_endpointVolume); _endpointVolume = null; }
            if (_sessionManager != null) { Marshal.ReleaseComObject(_sessionManager); _sessionManager = null; }
            if (_device != null) { Marshal.ReleaseComObject(_device); _device = null; }
            if (_enumerator != null) { Marshal.ReleaseComObject(_enumerator); _enumerator = null; }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Cleanup();
        }

        // ===== Config Persistence (like GpuPowerService) =====
        private static VolumeConfig LoadConfig()
        {
            var defaults = new VolumeConfig(DefaultMaxVolume, true);
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var cfg = JsonSerializer.Deserialize<VolumeConfig>(File.ReadAllText(ConfigPath));
                    if (cfg != null) return cfg with
                    {
                        MaxVolumeScalar = Math.Clamp(cfg.MaxVolumeScalar, 0.01f, 1.0f)
                    };
                }
            }
            catch { }
            WriteJson(ConfigPath, defaults);
            return defaults;
        }

        private static void WriteJson<T>(string path, T value)
        {
            Directory.CreateDirectory(StateDir);
            File.WriteAllText(path, JsonSerializer.Serialize(value));
        }

        // ===== Headless CLI Verbs (like GpuPowerService) =====

        /// <summary>Entry point for <c>CoreShift.exe --volume-apply</c> (runs at logon via scheduled task).</summary>
        public static int CliApply()
        {
            try
            {
                var config = LoadConfig();
                if (!config.Enabled) return 0;

                // Run on STA thread with proper COM initialization
                var tcs = new TaskCompletionSource<int>();
                var thread = new Thread(() =>
                {
                    try
                    {
                        // Initialize COM on this STA thread
                        int hr = CoInitializeEx(IntPtr.Zero, COINIT_APARTMENTTHREADED);
                        if (hr != S_OK && hr != S_FALSE && hr != unchecked((int)0x80010106)) // RPC_E_CHANGED_MODE
                        {
                            tcs.SetResult(1);
                            return;
                        }

                        try
                        {
                            var result = ApplyLimitOnce(config.MaxVolumeScalar);
                            tcs.SetResult(result.Success ? 0 : 1);
                        }
                        finally
                        {
                            CoUninitialize();
                        }
                    }
                    catch
                    {
                        tcs.SetResult(1);
                    }
                });
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                thread.Join();
                return tcs.Task.Result;
            }
            catch
            {
                return 1;
            }
        }

        /// <summary>Applies the volume limit once without registering callbacks (for CLI verb).</summary>
        private static PowerResult ApplyLimitOnce(float maxVolumeScalar)
        {
            IMMDeviceEnumerator? enumerator = null;
            IMMDevice? device = null;
            IAudioEndpointVolume? endpointVolume = null;
            IAudioSessionManager2? sessionManager = null;

            try
            {
                // 1. Create device enumerator using Activator.CreateInstance
                var enumeratorType = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"));
                if (enumeratorType == null) return PowerResult.Fail("Failed to get MMDeviceEnumerator type");
                var enumeratorInstance = Activator.CreateInstance(enumeratorType);
                if (enumeratorInstance == null) return PowerResult.Fail("Failed to create MMDeviceEnumerator");
                enumerator = (IMMDeviceEnumerator)enumeratorInstance;

                // 2. Get default render endpoint
                int hr = enumerator.GetDefaultAudioEndpoint(
                    (int)EDataFlow.eRender,
                    (int)ERole.eConsole,
                    out device);
                if (hr != S_OK)
                    return PowerResult.Fail($"GetDefaultAudioEndpoint failed: 0x{hr:X}");

                // 3. Activate IAudioEndpointVolume
                var iidEndpointVolume = IID_IAudioEndpointVolume;
                hr = device.Activate(ref iidEndpointVolume, CLSCTX_ALL, IntPtr.Zero, out var volPtr);
                if (hr != S_OK)
                    return PowerResult.Fail($"Activate IAudioEndpointVolume failed: 0x{hr:X}");
                endpointVolume = (IAudioEndpointVolume)Marshal.GetObjectForIUnknown(volPtr);
                Marshal.Release(volPtr);

                // 4. Activate IAudioSessionManager2
                var iidSessionManager = IID_IAudioSessionManager2;
                hr = device.Activate(ref iidSessionManager, CLSCTX_ALL, IntPtr.Zero, out var mgrPtr);
                if (hr != S_OK)
                    return PowerResult.Fail($"Activate IAudioSessionManager2 failed: 0x{hr:X}");
                sessionManager = (IAudioSessionManager2)Marshal.GetObjectForIUnknown(mgrPtr);
                Marshal.Release(mgrPtr);

                // 5. Apply limit to master volume
                endpointVolume.GetMasterVolumeLevelScalar(out float current);
                if (current > maxVolumeScalar)
                {
                    var emptyGuid = Guid.Empty;
                    endpointVolume.SetMasterVolumeLevelScalar(maxVolumeScalar, ref emptyGuid);
                }

                // 6. Apply limit to all existing sessions
                int hrEnum = sessionManager.GetSessionEnumerator(out IntPtr enumPtr);
                if (hrEnum != S_OK || enumPtr == IntPtr.Zero)
                    return PowerResult.Fail($"GetSessionEnumerator failed: 0x{hrEnum:X}");
                var sessionEnumerator = (IAudioSessionEnumerator)Marshal.GetObjectForIUnknown(enumPtr);
                Marshal.Release(enumPtr);
                sessionEnumerator.GetCount(out int count);

                for (int i = 0; i < count; i++)
                {
                    int hrSession = sessionEnumerator.GetSession(i, out IntPtr sessionPtr);
                    if (hrSession != S_OK || sessionPtr == IntPtr.Zero) continue;

                    // Get IAudioSessionControl2 first (for process ID)
                    var session2 = (IAudioSessionControl2)Marshal.GetObjectForIUnknown(sessionPtr);
                    Marshal.Release(sessionPtr);

                    try
                    {
                        session2.GetProcessId(out uint pid);

                        // Skip system sounds session (often has pid=0)
                        if (pid == 0) continue;

                        // Get ISimpleAudioVolume for volume control
                        var simpleVol = (ISimpleAudioVolume)session2;
                        simpleVol.GetMasterVolume(out float vol);
                        if (vol > maxVolumeScalar)
                        {
                            var emptyGuid = Guid.Empty;
                            simpleVol.SetMasterVolume(maxVolumeScalar, ref emptyGuid);
                        }
                    }
                    finally
                    {
                        if (session2 != null) Marshal.ReleaseComObject(session2);
                    }
                }
                if (sessionEnumerator != null) Marshal.ReleaseComObject(sessionEnumerator);

                return PowerResult.Ok();
            }
            catch (Exception ex)
            {
                return PowerResult.Fail($"Volume limiter apply failed: {ex.Message}");
            }
            finally
            {
                // Release COM objects in reverse order
                if (sessionManager != null) { try { Marshal.ReleaseComObject(sessionManager); } catch { } }
                if (endpointVolume != null) { try { Marshal.ReleaseComObject(endpointVolume); } catch { } }
                if (device != null) { try { Marshal.ReleaseComObject(device); } catch { } }
                if (enumerator != null) { try { Marshal.ReleaseComObject(enumerator); } catch { } }
            }
        }

        // COM initialization
        private const int COINIT_APARTMENTTHREADED = 0x2;
        private const int S_OK = 0;
        private const int S_FALSE = 1;

        [DllImport("ole32.dll", ExactSpelling = true, PreserveSig = true)]
        private static extern int CoInitializeEx(IntPtr pvReserved, int dwCoInit);

        [DllImport("ole32.dll", ExactSpelling = true, PreserveSig = true)]
        private static extern void CoUninitialize();

        /// <summary>Entry point for <c>CoreShift.exe --volume-install</c> (elevated once via UAC to register scheduled task).</summary>
        public static int CliInstall()
        {
            try
            {
                string exe = Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName;

                // Create scheduled task at logon (similar to GPU task but at logon instead of ONCE)
                // Using ONCE at 00:00 with /RL HIGHEST is not needed since no elevation required for per-user audio
                // But we'll use the same pattern for consistency: ONCE at 00:00, manually triggered
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = $"/Create /F /TN \"{TaskName}\" /TR \"\\\"{exe}\\\" --volume-apply\" /SC ONCE /ST 00:00 /RL HIGHEST",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
                };

                using var proc = System.Diagnostics.Process.Start(psi);
                proc?.WaitForExit(5000);

                if (proc?.ExitCode != 0)
                {
                    return 2;
                }

                // Apply immediately as well
                return CliApply();
            }
            catch
            {
                return 1;
            }
        }

    // ===== Callback Implementations =====

    /// <summary>Receives master volume change notifications.</summary>
    internal sealed class VolumeCallback : IAudioEndpointVolumeCallback
    {
        private readonly VolumeLimitService _service;
        public VolumeCallback(VolumeLimitService service) => _service = service;

        public int OnNotify(IntPtr pNotify)
        {
            try
            {
                var data = Marshal.PtrToStructure<AudioVolumeNotificationData>(pNotify);
                _service.OnMasterVolumeChanged(data.fMasterVolume, data.bMuted);
            }
            catch { }
            return S_OK;
        }
    }

    /// <summary>Receives new session creation notifications.</summary>
    internal sealed class SessionNotification : IAudioSessionNotification
    {
        private readonly VolumeLimitService _service;
        public SessionNotification(VolumeLimitService service) => _service = service;

        public int OnSessionCreated(IAudioSessionControl newSession)
        {
            _service.OnSessionCreated(newSession);
            return S_OK;
        }
    }

    /// <summary>Receives default audio endpoint change notifications.</summary>
    internal sealed class DeviceNotification : IMMNotificationClient
    {
        private readonly VolumeLimitService _service;
        public DeviceNotification(VolumeLimitService service) => _service = service;

        public int OnDeviceStateChanged(string pwstrDeviceId, int dwNewState) => S_OK;
        public int OnDeviceAdded(string pwstrDeviceId) => S_OK;
        public int OnDeviceRemoved(string pwstrDeviceId) => S_OK;

        public int OnDefaultDeviceChanged(int flow, int role, string pwstrDefaultDeviceId)
        {
            // Only care about render console endpoint changes
            if (flow == (int)EDataFlow.eRender && role == (int)ERole.eConsole)
            {
                _service.OnDefaultDeviceChanged();
            }
            return S_OK;
        }

        public int OnPropertyValueChanged(string pwstrDeviceId, int key) => S_OK;
    }
}
}