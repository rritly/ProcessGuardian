using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ProcessGuardian.Domain.Interfaces;
using ProcessGuardian.Infrastructure.Logging;
using ProcessGuardian.Infrastructure.Native;
using ProcessGuardian.Wpf.ViewModels;
using ProcessGuardian.Wpf.Views;

namespace ProcessGuardian.Wpf;

public partial class App : System.Windows.Application
{
    private IHost? _host;
    private MainWindow? _mainWindow;
    private ITrayService? _tray;
    private SingleInstanceManager? _singleInstance;
    private bool _userRequestedExit;

    protected override void OnStartup(StartupEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        try
        {
            base.OnStartup(e);
            InitializeApplication(e);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Fatal error during startup: {ex.Message}\n\n{ex.StackTrace}",
                "ProcessGuardian - Fatal Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void InitializeApplication(StartupEventArgs e)
    {
        _singleInstance = new SingleInstanceManager();
        if (!_singleInstance.IsFirstInstance())
        {
            _singleInstance.ActivateFirstInstance();
            _singleInstance.Dispose();
            Shutdown();
            return;
        }

        _singleInstance.ShowRequested += (_, __) => Dispatcher.BeginInvoke(() => ShowMainWindow());
        _singleInstance.StartWatching();

        _host = new HostBuilder()
            .ConfigureServices(Bootstrapper.Configure)
            .Build();

        // The window may be hidden in background mode; exit only via Shutdown().
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Wire tray (resolved on UI thread before the host starts)
        _tray = _host.Services.GetRequiredService<ITrayService>();
        _tray.ShowRequested += (_, __) => Dispatcher.BeginInvoke(() => ShowMainWindow());
        _tray.StartRequested += (_, __) => Dispatcher.BeginInvoke(() =>
        {
            var vm = _host.Services.GetRequiredService<MainViewModel>();
            if (vm.StartCommand.CanExecute(null))
                vm.StartCommand.Execute(null);
        });
        _tray.StopRequested += (_, __) => Dispatcher.BeginInvoke(() =>
        {
            var vm = _host.Services.GetRequiredService<MainViewModel>();
            if (vm.StopCommand.CanExecute(null))
                vm.StopCommand.Execute(null);
        });
        _tray.ExitRequested += (_, __) => Dispatcher.BeginInvoke(() =>
        {
            _userRequestedExit = true;
            if (_host != null)
            {
                try
                {
                    var vm = _host.Services.GetRequiredService<MainViewModel>();
                // Stop monitoring and persist BEFORE Shutdown so the next launch does
                // not auto-start. The save runs on the thread pool, so blocking is safe.
                Task.Run(() => vm.StopMonitoringAndSaveAsync()).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    CrashLog.Append("shutdown-error.log", $"[{DateTime.Now}] Stop monitoring on exit: {ex}\n");
                }
            }
            Shutdown();
        });
        _tray.Initialize();

        // Wire status -> tray icon
        var statusReporter = _host.Services.GetRequiredService<IStatusReporter>();
        statusReporter.StatusChanged += (_, status) =>
            Dispatcher.BeginInvoke(() => _tray.SetStatus(status));

        // Resolve the VM (singleton) before starting the host so its status/log
        // subscriptions exist before the orchestrator's first Report().
        _host.Services.GetRequiredService<MainViewModel>();

        // Do NOT block the UI thread on StartAsync: the orchestrator marshals status to
        // the Dispatcher, so waiting on StartAsync here would deadlock. Fire-and-forget.
        StartHostAsync();

        // Background mode? Show the window immediately after host start, without
        // waiting for the monitoring cycle to complete.
        var isBackground = e.Args.Any(a => a.Equals("--background", StringComparison.OrdinalIgnoreCase));
        if (!isBackground)
        {
            ShowMainWindow();
        }
    }

    private void StartHostAsync()
    {
        Task.Run(async () =>
        {
            try
            {
                await _host!.StartAsync();
            }
            catch (Exception ex)
            {
                CrashLog.Append("crash.log", $"[{DateTime.Now}] Host start fault: {ex}\n");
            }
        });
    }

    private void ShowMainWindow()
    {
        try
        {
            if (_mainWindow == null)
            {
                _mainWindow = _host!.Services.GetRequiredService<MainWindow>();
                _mainWindow.DataContext = _host.Services.GetRequiredService<MainViewModel>();
                _mainWindow.Closing += OnMainWindowClosing;
                _mainWindow.Closed += OnMainWindowClosed;
                _mainWindow.ShowInTaskbar = true;
                _mainWindow.Show();
            }
            else
            {
                _mainWindow.Show();
                _mainWindow.WindowState = WindowState.Normal;
                _mainWindow.ShowInTaskbar = true;
                _mainWindow.Activate();
            }
        }
        catch (Exception ex)
        {
            try
            {
                MessageBox.Show(
                    $"Error showing main window: {ex.Message}",
                    "ProcessGuardian - Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            catch { }
        }
    }

    // Close-to-tray: hide the window instead of closing; exit only via the tray menu.
    private void OnMainWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        if (sender is Window w)
        {
            try
            {
                w.Hide();
                w.ShowInTaskbar = false;
            }
            catch { }
        }
    }

    private void OnMainWindowClosed(object? sender, EventArgs e)
    {
        _mainWindow = null;
    }

    // async void: every await below catches its own errors, so shutdown never aborts.
    protected override async void OnExit(ExitEventArgs e)
    {
        // User-initiated Exit disables monitoring so it does not auto-start on reboot.
        // Reboot/logoff (non-user exit) leaves the flag untouched.
        if (_userRequestedExit && _host != null)
        {
            try
            {
                var vm = _host.Services.GetRequiredService<MainViewModel>();
                vm.Settings.IsMonitoringEnabled = false;
            }
            catch (Exception ex)
            {
                CrashLog.Append("shutdown-error.log", $"[{DateTime.Now}] Disable monitoring on exit: {ex}\n");
            }
        }

        // Dispose ViewModel explicitly since WPF does not call DisposeAsync on DataContext.
        try
        {
            if (_host != null)
            {
                var vm = _host.Services.GetRequiredService<MainViewModel>();
                await vm.DisposeAsync();
            }
        }
        catch (Exception ex)
        {
            CrashLog.Append("shutdown-error.log", $"[{DateTime.Now}] VM dispose: {ex}\n");
        }

        try
        {
            _tray?.Dispose();
        }
        catch (Exception ex)
        {
            CrashLog.Append("shutdown-error.log", $"[{DateTime.Now}] Tray dispose: {ex}\n");
        }

        try
        {
            if (_host != null)
                await _host.StopAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            CrashLog.Append("shutdown-error.log", $"[{DateTime.Now}] Host stop: {ex}\n");
        }

        try
        {
            _host?.Dispose();
        }
        catch (Exception ex)
        {
            CrashLog.Append("shutdown-error.log", $"[{DateTime.Now}] Host dispose: {ex}\n");
        }

        try
        {
            _singleInstance?.Dispose();
        }
        catch { }

        base.OnExit(e);
    }

    // Fatal CLR exception: notify and exit; graceful cleanup is not possible here.
    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception;
        CrashLog.Append("crash.log", $"[{DateTime.Now}] FATAL: {ex}\n");
        try
        {
            MessageBox.Show(
                $"Unhandled exception: {ex?.Message}\nThe application will now exit.",
                "ProcessGuardian - Fatal Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch { }
        Environment.Exit(1);
    }

    // Policy: swallow UI exceptions to keep the app running; only crash.log records them.
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        CrashLog.Append("crash.log", $"[{DateTime.Now}] UI FATAL: {e.Exception}\n");
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved();
        CrashLog.Append("crash.log", $"[{DateTime.Now}] TASK FATAL: {e.Exception}\n");
    }
}
