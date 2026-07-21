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

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
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

    private async void GpuHeavy_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.SetGpuModeAsync(GpuMode.Heavy);
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
