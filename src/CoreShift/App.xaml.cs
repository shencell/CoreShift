using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CoreShift.Services;
using CoreShift.ViewModels;
using H.NotifyIcon;

namespace CoreShift;

/// <summary>
/// Application entry point. Runs as a pure background process: no main window is shown
/// on launch, only a system-tray icon. The widget window is rendered on demand.
/// </summary>
public partial class App : Application
{
    private const string MutexName = "CoreShift.SingleInstance.8F3A1C2E-2D4B-4C1A-9E77-A1B2C3D4E5F6";

    private Mutex? _instanceMutex;
    private TaskbarIcon? _tray;
    private MainWindow? _window;
    private MainViewModel? _viewModel;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Single-instance guard: a second launch simply exits.
        _instanceMutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown(0);
            return;
        }

        base.OnStartup(e);

        // Phase 1: self-register into HKCU\...\Run on first ever launch (per spec).
        try { StartupService.EnsureFirstRun(); }
        catch { /* startup registration is best-effort, never fatal */ }

        _viewModel = new MainViewModel(new PowerService());
        _window = new MainWindow(_viewModel);

        _tray = new TaskbarIcon
        {
            ToolTipText = "CoreShift — power-mode toggle",
            IconSource = new BitmapImage(new Uri("pack://application:,,,/Assets/CoreShift.ico")),
            ContextMenu = BuildContextMenu()
        };
        _tray.TrayLeftMouseUp += (_, _) => ShowWidget();
        _tray.ForceCreate();

        // Once the startup JIT/loading work drains, hand the initial working set back to the OS.
        Dispatcher.BeginInvoke(MemoryUtil.TrimWorkingSet, DispatcherPriority.ApplicationIdle);
    }

    private ContextMenu BuildContextMenu()
    {
        var open = new MenuItem { Header = "Open" };
        open.Click += (_, _) => ShowWidget();

        var startWithWindows = new MenuItem { Header = "Start with Windows", IsCheckable = true };
        startWithWindows.Click += (_, _) =>
        {
            try
            {
                if (startWithWindows.IsChecked) StartupService.Enable();
                else StartupService.Disable();
            }
            catch { /* ignore registry failures */ }
        };

        var exit = new MenuItem { Header = "Exit" };
        exit.Click += (_, _) => Shutdown();

        var menu = new ContextMenu();
        menu.Items.Add(open);
        menu.Items.Add(startWithWindows);
        menu.Items.Add(new Separator());
        menu.Items.Add(exit);

        // Reflect the live registry state every time the menu opens.
        menu.Opened += (_, _) =>
        {
            try { startWithWindows.IsChecked = StartupService.IsEnabled(); }
            catch { /* ignore */ }
        };

        return menu;
    }

    /// <summary>
    /// Phase 2 + 3: query the current processor state, position the widget in the
    /// bottom-right corner just above the tray, and render it.
    /// </summary>
    private async void ShowWidget()
    {
        if (_window is null || _viewModel is null) return;

        await _viewModel.RefreshAsync();

        TrayPositioner.PlaceBottomRight(_window);
        _window.Show();
        _window.Activate();
        _window.Topmost = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();

        if (_instanceMutex is not null)
        {
            try { _instanceMutex.ReleaseMutex(); } catch { /* not owned */ }
            _instanceMutex.Dispose();
        }

        base.OnExit(e);
    }
}
