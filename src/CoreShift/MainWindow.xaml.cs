using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using CoreShift.Services;
using CoreShift.ViewModels;

namespace CoreShift;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private DispatcherTimer? _volumeDebounceTimer;

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        // Debounce timer for volume slider changes
        _volumeDebounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(300)
        };
        _volumeDebounceTimer.Tick += async (_, _) =>
        {
            _volumeDebounceTimer.Stop();
            await _viewModel.SetVolumeLimitAsync();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Hide the frameless widget from Alt+Tab and the taskbar by marking it a tool window.
        IntPtr handle = new WindowInteropHelper(this).Handle;
        int exStyle = GetWindowLong(handle, GWL_EXSTYLE);
        SetWindowLong(handle, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);
    }

    // Phase 4: dismiss the widget as soon as it loses focus (user clicks elsewhere).
    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        Hide();

        // Once hidden, collect managed garbage and hand the working set back to the OS.
        Dispatcher.BeginInvoke(() =>
        {
            GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
            MemoryUtil.TrimWorkingSet();
        }, DispatcherPriority.Background);
    }

    private async void ToggleButton_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.ToggleAsync();
    }

    private async void GpuBase_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.SetGpuModeAsync(GpuMode.Base);
    }

    private async void GpuLight_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.SetGpuModeAsync(GpuMode.Light);
    }

    private async void GpuFirm_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.SetGpuModeAsync(GpuMode.Firm);
    }

    private async void GpuHeavy_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.SetGpuModeAsync(GpuMode.Heavy);
    }

    // Volume Limit event handlers
    private async void VolumeLockButton_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.ToggleVolumeLimitAsync();
    }

    private void VolumeLimitSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Debounce: only apply after user stops dragging for 300ms
        _volumeDebounceTimer?.Stop();
        _volumeDebounceTimer?.Start();
    }

    private async void VolumeLimitTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        _volumeDebounceTimer?.Stop();
        if (sender is System.Windows.Controls.TextBox tb)
        {
            if (int.TryParse(tb.Text, out int value))
            {
                _viewModel.VolumeLimitPercentText = Math.Clamp(value, 1, 100).ToString();
                await _viewModel.SetVolumeLimitAsync();
            }
            else
            {
                // Reset to current valid value
                tb.Text = _viewModel.VolumeLimitPercentText;
            }
        }
    }

    private async void VolumeLimitTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter && sender is System.Windows.Controls.TextBox tb)
        {
            _volumeDebounceTimer?.Stop();
            if (int.TryParse(tb.Text, out int value))
            {
                _viewModel.VolumeLimitPercentText = Math.Clamp(value, 1, 100).ToString();
                await _viewModel.SetVolumeLimitAsync();
            }
            else
            {
                tb.Text = _viewModel.VolumeLimitPercentText;
            }
            tb.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.Next));
        }
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
