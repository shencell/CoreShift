using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;

namespace CoreShift.Services;

/// <summary>Software power presets for the NVIDIA dGPU.</summary>
public enum GpuMode
{
    /// <summary>No CoreShift cap: locked clocks released, power limit raised to the vBIOS
    /// maximum — Armoury Crate governs.</summary>
    Base,
    /// <summary>Aggressive cap for light sessions (~40–60 W effective).</summary>
    Light,
    /// <summary>Middle cap between LIGHT and HEAVY (~50–70 W effective).</summary>
    Firm,
    /// <summary>Moderate cap for heavy sessions (~60–80 W effective).</summary>
    Heavy,
    /// <summary>No preset applied this boot (or applied state unknown).</summary>
    Custom
}

/// <summary>Live GPU readings from NVML.</summary>
public readonly record struct GpuState(int CurrentLimitWatts, int MinWatts, int MaxWatts, int GraphicsClockMhz);

/// <summary>
/// Caps the NVIDIA GPU through NVML (<c>nvml.dll</c>). Preferred knob is the software power
/// limit; laptop vBIOSes commonly refuse it (NVML_ERROR_NOT_SUPPORTED), so the fallback is
/// locked GPU clocks (<c>nvmlDeviceSetGpuLockedClocks</c>, the <c>nvidia-smi -lgc</c> API),
/// which achieves a comparable thermal cap.
///
/// Reading never needs elevation, so the widget always shows live data. Writing does, which
/// clashes with CoreShift's asInvoker design — so writes go through a one-time-registered
/// scheduled task ("CoreShift GPU Limit", RunLevel Highest): the UI stores the target mode and
/// a nonce in <c>%LOCALAPPDATA%\CoreShift\gpu-target.json</c>, starts the task (no UAC for the
/// task's own creator), and a short-lived elevated CoreShift instance (<c>--gpu-apply</c>) does
/// the NVML calls and echoes the nonce back through <c>gpu-result.json</c>. Only the very first
/// setup (<c>--gpu-install</c>) shows a UAC prompt. Plain files are used instead of HKCU on
/// purpose: an elevated task process is not guaranteed to see the same user hive (profile-less
/// task starts, registry-virtualizing sandboxes), while %LOCALAPPDATA% is one shared truth.
///
/// No ASUS ACPI/WMI is touched, so this coexists with Armoury Crate. Both NVML knobs reset on
/// reboot; the applied mode is therefore remembered per boot session only.
/// </summary>
public sealed class GpuPowerService
{
    private const string TaskName = "CoreShift GPU Limit";

    private static string StateDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CoreShift");
    private static string ConfigPath => Path.Combine(StateDir, "gpu-config.json");
    private static string TargetPath => Path.Combine(StateDir, "gpu-target.json");
    private static string ResultPath => Path.Combine(StateDir, "gpu-result.json");

    private sealed record GpuConfig(
        int LightWatts, int FirmWatts, int HeavyWatts,
        int LightMaxMhz, int FirmMaxMhz, int HeavyMaxMhz);
    private sealed record GpuTarget(string Mode, string Nonce);
    private sealed record GpuOutcome(string Echo, string Result, string AppliedMode, string AppliedBoot);

    // LIGHT targets ~40–60 W, FIRM ~50–70 W, HEAVY ~60–80 W. Watts are tried first (power
    // limit); the MHz values are the locked-clock fallback tuned to land in the same thermal
    // envelope on a 100–140 W-class mobile GPU. All six are seeded into gpu-config.json.
    private const int DefaultLightWatts = 50;
    private const int DefaultFirmWatts = 60;
    private const int DefaultHeavyWatts = 70;
    private const int DefaultLightMaxMhz = 990;
    private const int DefaultFirmMaxMhz = 1200;
    private const int DefaultHeavyMaxMhz = 1402;
    private const uint LockedClockFloorMhz = 210;

    /// <summary>The preset applied in this boot session (Custom when none / stale).</summary>
    public GpuMode GetAppliedMode()
    {
        GpuOutcome? outcome = ReadJson<GpuOutcome>(ResultPath);
        if (outcome is null || outcome.AppliedBoot != BootStamp())
            return GpuMode.Custom;

        return outcome.AppliedMode switch
        {
            "base" => GpuMode.Base,
            "light" => GpuMode.Light,
            "firm" => GpuMode.Firm,
            "heavy" => GpuMode.Heavy,
            _ => GpuMode.Custom
        };
    }

    /// <summary>Reads live limit/clock data. Null state (with a reason) when NVML is unavailable.</summary>
    public Task<(GpuState? State, string Error)> GetStateAsync() => Task.Run(() =>
    {
        try
        {
            return Nvml.WithDevice((dev) =>
            {
                int rc = Nvml.GetLimit(dev, out uint mw);
                if (rc != 0) return ((GpuState?)null, Nvml.Describe(rc));

                rc = Nvml.GetConstraints(dev, out uint minMw, out uint maxMw);
                if (rc != 0) return (null, Nvml.Describe(rc));

                // Clock read is cosmetic — ignore failures.
                Nvml.GetClock(dev, Nvml.ClockGraphics, out uint mhz);

                return (new GpuState((int)(mw / 1000), (int)(minMw / 1000), (int)(maxMw / 1000), (int)mhz),
                        string.Empty);
            });
        }
        catch (Exception ex)
        {
            return (null, ex.GetBaseException().Message);
        }
    });

    /// <summary>
    /// Applies a preset from the (unelevated) UI. Stores the target, then either runs the
    /// scheduled task or — on first use — installs it via a single elevated relaunch.
    /// </summary>
    public Task<PowerResult> ApplyModeAsync(GpuMode mode) => Task.Run(() =>
    {
        string nonce = Guid.NewGuid().ToString("N");
        WriteJson(TargetPath, new GpuTarget(ModeName(mode), nonce));

        if (!TaskExists())
        {
            PowerResult installed = RunElevatedInstall();
            if (!installed.Success) return installed;
        }
        else
        {
            ProcResult run = RunProcess("schtasks.exe", $"/Run /TN \"{TaskName}\"");
            if (run.ExitCode != 0)
                return PowerResult.Fail($"Could not start the helper task: {run.StdErr}".Trim());
        }

        return AwaitHelperResult(nonce);
    });

    /// <summary>Entry point for <c>CoreShift.exe --gpu-apply</c> (runs elevated via the task).</summary>
    public static int CliApply()
    {
        GpuTarget target = ReadJson<GpuTarget>(TargetPath) ?? new GpuTarget("base", string.Empty);
        GpuMode mode = target.Mode switch
        {
            "light" => GpuMode.Light,
            "firm" => GpuMode.Firm,
            "heavy" => GpuMode.Heavy,
            _ => GpuMode.Base
        };
        string nonce = target.Nonce;

        GpuConfig cfg = LoadConfig();
        int lightWatts = Math.Clamp(cfg.LightWatts, 40, 60);
        int firmWatts = Math.Clamp(cfg.FirmWatts, 50, 70);
        int heavyWatts = Math.Clamp(cfg.HeavyWatts, 60, 80);
        int lightMhz = Math.Clamp(cfg.LightMaxMhz, 400, 1300);
        int firmMhz = Math.Clamp(cfg.FirmMaxMhz, 800, 1600);
        int heavyMhz = Math.Clamp(cfg.HeavyMaxMhz, 1000, 2000);

        string error;
        string method = "none";
        try
        {
            (_, error) = Nvml.WithDevice<object?>((dev) =>
            {
                if (mode == GpuMode.Base)
                {
                    // Release both knobs; either may be unsupported — succeed if any resets.
                    int rcClock = Nvml.ResetLockedClocks(dev);
                    int rcPower = 1;
                    if (Nvml.GetConstraints(dev, out _, out uint maxMw) == 0)
                        rcPower = Nvml.SetLimit(dev, maxMw);

                    method = rcClock == 0 ? "clock" : "power";
                    return (null, rcClock == 0 || rcPower == 0 ? string.Empty : Nvml.Describe(rcClock));
                }

                int watts = mode switch
                {
                    GpuMode.Light => lightWatts,
                    GpuMode.Firm => firmWatts,
                    _ => heavyWatts
                };
                uint maxMhz = (uint)(mode switch
                {
                    GpuMode.Light => lightMhz,
                    GpuMode.Firm => firmMhz,
                    _ => heavyMhz
                });

                // Preferred knob: software power limit.
                if (Nvml.GetConstraints(dev, out uint minMwC, out uint maxMwC) == 0)
                {
                    uint mw = Math.Clamp((uint)watts * 1000u, minMwC, maxMwC);
                    if (Nvml.SetLimit(dev, mw) == 0)
                    {
                        Nvml.ResetLockedClocks(dev);   // don't stack both caps
                        method = "power";
                        return (null, string.Empty);
                    }
                }

                // Fallback: locked clocks (laptop vBIOSes usually allow this one).
                int rc = Nvml.SetLockedClocks(dev, LockedClockFloorMhz, maxMhz);
                if (rc == 0)
                {
                    method = "clock";
                    return (null, string.Empty);
                }

                return (null, Nvml.Describe(rc));
            });
        }
        catch (Exception ex)
        {
            error = ex.GetBaseException().Message;
        }

        bool ok = error.Length == 0;
        GpuOutcome previous = ReadJson<GpuOutcome>(ResultPath) ?? new GpuOutcome("", "", "", "");
        WriteJson(ResultPath, new GpuOutcome(
            Echo: nonce,
            Result: ok ? $"ok ({method})" : error,
            AppliedMode: ok ? ModeName(mode) : previous.AppliedMode,
            AppliedBoot: ok ? BootStamp() : previous.AppliedBoot));

        return ok ? 0 : 1;
    }

    /// <summary>Entry point for <c>CoreShift.exe --gpu-install</c> (elevated once, via UAC).</summary>
    public static int CliInstall()
    {
        string exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName;

        // ONCE at 00:00 (already past) = never fires on its own; it exists purely to be
        // started on demand by its owner, which Windows allows without another UAC prompt.
        ProcResult create = RunProcess("schtasks.exe",
            $"/Create /F /TN \"{TaskName}\" /TR \"\\\"{exe}\\\" --gpu-apply\" /SC ONCE /ST 00:00 /RL HIGHEST");
        if (create.ExitCode != 0) return 2;

        return CliApply();
    }

    private static string ModeName(GpuMode mode) => mode switch
    {
        GpuMode.Light => "light",
        GpuMode.Firm => "firm",
        GpuMode.Heavy => "heavy",
        _ => "base"
    };

    /// <summary>Identifies the current boot session (locked clocks don't survive reboots).</summary>
    private static string BootStamp()
    {
        DateTime boot = DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
        return boot.ToString("yyyyMMddHHmm");   // minute granularity absorbs clock jitter
    }

    private static bool TaskExists() =>
        RunProcess("schtasks.exe", $"/Query /TN \"{TaskName}\"").ExitCode == 0;

    private static PowerResult RunElevatedInstall()
    {
        string exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName;
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = "--gpu-install",
            UseShellExecute = true,
            Verb = "runas"
        };

        try
        {
            using Process? p = Process.Start(psi);
            p?.WaitForExit(30_000);
            return p is { ExitCode: 0 }
                ? PowerResult.Ok()
                : PowerResult.Fail("One-time GPU setup failed (task registration).");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return PowerResult.Fail("Setup cancelled — GPU limits need one-time admin approval.");
        }
    }

    /// <summary>Waits for the elevated helper to echo our nonce back through the result file.</summary>
    private static PowerResult AwaitHelperResult(string nonce)
    {
        for (int i = 0; i < 80; i++)   // up to ~8 s
        {
            System.Threading.Thread.Sleep(100);
            GpuOutcome? outcome = ReadJson<GpuOutcome>(ResultPath);
            if (outcome?.Echo == nonce)
            {
                return outcome.Result.StartsWith("ok")
                    ? PowerResult.Ok()
                    : PowerResult.Fail($"GPU: {outcome.Result}");
            }
        }
        return PowerResult.Fail("GPU helper did not respond (task blocked?).");
    }

    /// <summary>
    /// Loads the tuning config, seeding the file with defaults on first use. Any field that is
    /// missing or non-positive is healed back to its default and the file is rewritten — this is
    /// what upgrades a pre-FIRM config (which has no FirmWatts/FirmMaxMhz, so they would
    /// deserialize as 0 and clamp to the bottom of the band) and repairs bad hand-edits.
    /// </summary>
    private static GpuConfig LoadConfig()
    {
        var defaults = new GpuConfig(
            DefaultLightWatts, DefaultFirmWatts, DefaultHeavyWatts,
            DefaultLightMaxMhz, DefaultFirmMaxMhz, DefaultHeavyMaxMhz);

        GpuConfig? cfg = ReadJson<GpuConfig>(ConfigPath);
        if (cfg is null)
        {
            WriteJson(ConfigPath, defaults);
            return defaults;
        }

        GpuConfig healed = cfg with
        {
            LightWatts = cfg.LightWatts > 0 ? cfg.LightWatts : defaults.LightWatts,
            FirmWatts = cfg.FirmWatts > 0 ? cfg.FirmWatts : defaults.FirmWatts,
            HeavyWatts = cfg.HeavyWatts > 0 ? cfg.HeavyWatts : defaults.HeavyWatts,
            LightMaxMhz = cfg.LightMaxMhz > 0 ? cfg.LightMaxMhz : defaults.LightMaxMhz,
            FirmMaxMhz = cfg.FirmMaxMhz > 0 ? cfg.FirmMaxMhz : defaults.FirmMaxMhz,
            HeavyMaxMhz = cfg.HeavyMaxMhz > 0 ? cfg.HeavyMaxMhz : defaults.HeavyMaxMhz
        };

        if (healed != cfg) WriteJson(ConfigPath, healed);   // records give value equality
        return healed;
    }

    private static T? ReadJson<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : null;
        }
        catch
        {
            return null;   // corrupt/locked file behaves like "absent"
        }
    }

    private static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(StateDir);
        File.WriteAllText(path, JsonSerializer.Serialize(value));
    }

    private static ProcResult RunProcess(string fileName, string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        try
        {
            using Process? p = Process.Start(psi);
            if (p is null) return new ProcResult(-1, string.Empty, $"Failed to start {fileName}.");
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();
            return new ProcResult(p.ExitCode, stdout, stderr);
        }
        catch (Exception ex)
        {
            return new ProcResult(-1, string.Empty, ex.Message);
        }
    }

    private readonly record struct ProcResult(int ExitCode, string StdOut, string StdErr);

    /// <summary>
    /// Minimal NVML binding. nvml.dll is not in System32 on laptop drivers — it lives in the
    /// DriverStore folder of the active driver (path changes on every driver update), so a
    /// custom resolver picks the newest folder that ships the DLL.
    /// </summary>
    private static class Nvml
    {
        private const string Lib = "nvml";

        public const uint ClockGraphics = 0;   // NVML_CLOCK_GRAPHICS

        static Nvml() => NativeLibrary.SetDllImportResolver(typeof(Nvml).Assembly, Resolve);

        private static IntPtr Resolve(string name, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (name != Lib) return IntPtr.Zero;

            if (NativeLibrary.TryLoad("nvml.dll", out IntPtr handle))
                return handle;

            string repo = Path.Combine(Environment.SystemDirectory, "DriverStore", "FileRepository");
            string? dll = Directory.Exists(repo)
                ? Directory.EnumerateDirectories(repo, "nv*")
                    .Select(dir => Path.Combine(dir, "nvml.dll"))
                    .Where(File.Exists)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault()
                : null;

            return dll is not null && NativeLibrary.TryLoad(dll, out handle) ? handle : IntPtr.Zero;
        }

        /// <summary>Init → device 0 → <paramref name="body"/> → shutdown, with error mapping.</summary>
        public static (T?, string) WithDevice<T>(Func<IntPtr, (T?, string)> body)
        {
            int rc = Init();
            if (rc != 0) return (default, Describe(rc));

            try
            {
                rc = GetHandle(0, out IntPtr device);
                return rc != 0 ? (default, Describe(rc)) : body(device);
            }
            finally
            {
                Shutdown();
            }
        }

        public static string Describe(int rc)
        {
            string text = Marshal.PtrToStringAnsi(ErrorString(rc)) ?? $"NVML error {rc}";
            return rc switch
            {
                3 => $"{text} — this GPU's vBIOS refuses the change",
                4 => $"{text} — the helper must run elevated",
                _ => text
            };
        }

        [DllImport(Lib, EntryPoint = "nvmlInit_v2")] private static extern int Init();
        [DllImport(Lib, EntryPoint = "nvmlShutdown")] private static extern int Shutdown();
        [DllImport(Lib, EntryPoint = "nvmlDeviceGetHandleByIndex_v2")] private static extern int GetHandle(uint index, out IntPtr device);
        [DllImport(Lib, EntryPoint = "nvmlDeviceGetPowerManagementLimit")] public static extern int GetLimit(IntPtr device, out uint milliwatts);
        [DllImport(Lib, EntryPoint = "nvmlDeviceGetPowerManagementLimitConstraints")] public static extern int GetConstraints(IntPtr device, out uint minMw, out uint maxMw);
        [DllImport(Lib, EntryPoint = "nvmlDeviceSetPowerManagementLimit")] public static extern int SetLimit(IntPtr device, uint milliwatts);
        [DllImport(Lib, EntryPoint = "nvmlDeviceGetClockInfo")] public static extern int GetClock(IntPtr device, uint clockType, out uint mhz);
        [DllImport(Lib, EntryPoint = "nvmlDeviceSetGpuLockedClocks")] public static extern int SetLockedClocks(IntPtr device, uint minMhz, uint maxMhz);
        [DllImport(Lib, EntryPoint = "nvmlDeviceResetGpuLockedClocks")] public static extern int ResetLockedClocks(IntPtr device);
        [DllImport(Lib, EntryPoint = "nvmlErrorString")] private static extern IntPtr ErrorString(int result);
    }
}
