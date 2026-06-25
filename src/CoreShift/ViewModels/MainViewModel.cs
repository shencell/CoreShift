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

    private int _maxProcessorState = 100;
    private bool _isBusy;
    private string _statusMessage = string.Empty;

    public MainViewModel(PowerService power) => _power = power;

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

    /// <summary>Reads the live state from Windows before the widget is shown.</summary>
    public async Task RefreshAsync()
    {
        StatusMessage = string.Empty;
        int? value = await _power.GetMaxProcessorStateAsync();
        if (value.HasValue)
            MaxProcessorState = value.Value;
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
