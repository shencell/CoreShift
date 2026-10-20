using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using CoreShift.Services;

namespace CoreShift.ViewModels;

/// <summary>
/// Backs the widget. Exposes the current processor cap and derived presentation state;
/// XAML DataTriggers swap the vector icon based on <see cref="IsCodingMode"/>.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly PowerService _power;
    private readonly GpuPowerService _gpu;

    private int _maxProcessorState = 100;
    private bool _isBusy;
    private string _statusMessage = string.Empty;
    private GpuState? _gpuState;
    private string _gpuError = string.Empty;

    public MainViewModel(PowerService power, GpuPowerService gpu)
    {
        _power = power;
        _gpu = gpu;
    }

    /// <summary>Current "Maximum processor state" percentage (100 = Coding, 99 = Gaming).</summary>
    public int MaxProcessorState
    {
        get => _maxProcessorState;
        private set
        {
            if (_maxProcessorState == value) return;
            _maxProcessorState = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsCodingMode));
            OnPropertyChanged(nameof(ModeTitle));
            OnPropertyChanged(nameof(ModeSubtitle));
        }
    }

    /// <summary>True at 100% (Turbo Boost on). Drives the PowerShell vs. Controller icon.</summary>
    public bool IsCodingMode => MaxProcessorState >= 100;

    public string ModeTitle => IsCodingMode ? "Coding Mode" : "Gaming Mode";

    public string ModeSubtitle => IsCodingMode
        ? "100%  ·  Turbo Boost ON"
        : "99%  ·  Turbo Boost OFF";

    public bool IsBusy
    {
        get => _isBusy;
        private set { if (_isBusy != value) { _isBusy = value; OnPropertyChanged(); } }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set { if (_statusMessage != value) { _statusMessage = value; OnPropertyChanged(); } }
    }

    /// <summary>Current GPU readings (null while unknown / NVML unavailable).</summary>
    public GpuState? GpuState
    {
        get => _gpuState;
        private set
        {
            _gpuState = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsGpuBase));
            OnPropertyChanged(nameof(IsGpuLight));
            OnPropertyChanged(nameof(IsGpuFirm));
            OnPropertyChanged(nameof(IsGpuHeavy));
            OnPropertyChanged(nameof(GpuStatusLine));
        }
    }

    /// <summary>Preset applied in this boot session, as recorded by the elevated helper.</summary>
    public GpuMode CurrentGpuMode { get; private set; } = GpuMode.Custom;

    public bool IsGpuBase => CurrentGpuMode == GpuMode.Base;
    public bool IsGpuLight => CurrentGpuMode == GpuMode.Light;
    public bool IsGpuFirm => CurrentGpuMode == GpuMode.Firm;
    public bool IsGpuHeavy => CurrentGpuMode == GpuMode.Heavy;

    public string GpuStatusLine
    {
        get
        {
            if (!GpuState.HasValue)
            {
                // Remove text display when GPU state unavailable (e.g., no NVIDIA GPU)
                return string.Empty;
            }

            GpuState s = GpuState.Value;
            return CurrentGpuMode switch
            {
                GpuMode.Light or GpuMode.Firm or GpuMode.Heavy => $"GPU  ·  {s.GraphicsClockMhz} MHz  ·  capped",
                GpuMode.Base => $"GPU  ·  {s.GraphicsClockMhz} MHz  ·  Armoury Crate governs",
                _ => $"GPU  ·  {s.GraphicsClockMhz} MHz  ·  no preset this boot"
            };
        }
    }

    /// <summary>Reads the live state from Windows before the widget is shown.</summary>
    public async Task RefreshAsync()
    {
        StatusMessage = string.Empty;
        int? value = await _power.GetMaxProcessorStateAsync();
        if (value.HasValue)
            MaxProcessorState = value.Value;

        await RefreshGpuAsync();
    }

    private async Task RefreshGpuAsync()
    {
        CurrentGpuMode = _gpu.GetAppliedMode();
        OnPropertyChanged(nameof(CurrentGpuMode));

        (GpuState? state, string error) = await _gpu.GetStateAsync();
        _gpuError = error;
        GpuState = state;   // setter re-raises the derived properties
    }

    /// <summary>Applies a GPU power preset (BASE / LIGHT / FIRM / HEAVY) and re-reads the result.</summary>
    public async Task SetGpuModeAsync(GpuMode mode)
    {
        if (IsBusy) return;

        IsBusy = true;
        StatusMessage = string.Empty;
        try
        {
            PowerResult result = await _gpu.ApplyModeAsync(mode);
            if (!result.Success)
                StatusMessage = result.Message;

            await RefreshGpuAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Flips between 100% and 99% and re-reads the result.</summary>
    public async Task ToggleAsync()
    {
        if (IsBusy) return;

        IsBusy = true;
        StatusMessage = string.Empty;
        try
        {
            int target = IsCodingMode ? 99 : 100;
            PowerResult result = await _power.SetMaxProcessorStateAsync(target);

            if (result.Success)
            {
                int? confirmed = await _power.GetMaxProcessorStateAsync();
                MaxProcessorState = confirmed ?? target;
            }
            else
            {
                StatusMessage = result.Message;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
