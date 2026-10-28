using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ProcessGuardian.Domain;
using ProcessGuardian.Domain.Enums;
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
                $"{AppEdition.Name} - Fatal Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static readonly string RefusedTrayNotice =
        $"{AppEdition.Name}: configuration is unavailable. An administrator must repair the elevated configuration " +
        "(owner BUILTIN\\Administrators or SYSTEM; no reparse points) or the recovery state at " +
        "HKLM\\SOFTWARE\\ProcessGuardianAdmin\\Recovery, then start the application again.";

    private static readonly string ResetTrayNotice =
        $"{AppEdition.Name}: settings were reset to safe defaults — the previous configuration was untrusted and " +
        "was quarantined. Monitoring is off and the target pin is cleared; select and re-pin the target to start.";

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
        _tray.ExitRequested += (_, __) => Dispatcher.BeginInvoke(async () =>
        {
            _userRequestedExit = true;
            if (_host != null)
            {
                try
                {
                    var vm = _host.Services.GetRequiredService<MainViewModel>();
                    // Stop monitoring and persist BEFORE Shutdown so the next launch does
                    // not auto-start. Awaited (not blocked on) so the dispatcher keeps
                    // servicing queued work during the save/autostart sync.
                    await vm.StopMonitoringAndSaveAsync();
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

        var isBackground = e.Args.Any(a => a.Equals("--background", StringComparison.OrdinalIgnoreCase));

        // Non-fatal config initialization, then boot: a refused store must NOT reach the
        // fatal MessageBox, must NOT start the host or any monitoring, and surfaces an
        // ordinary Error indicator (tray-only for --background). Initialization I/O runs
        // off the UI thread; the rest of this method resumes on the dispatcher.
        _ = RunBootAsync(isBackground);
    }

    private async Task RunBootAsync(bool isBackground)
    {
        ConfigurationInitializationStatus initStatus;
        try
        {
            var store = _host!.Services.GetRequiredService<IConfigurationStore>();
            initStatus = await Task.Run(() => store.InitializeAsync(CancellationToken.None));
        }
        catch (Exception ex)
        {
            CrashLog.Append("crash.log", $"[{DateTime.Now}] Config initialization fault: {ex}\n");
            initStatus = ConfigurationInitializationStatus.Refused;
        }

        if (initStatus == ConfigurationInitializationStatus.Refused)
        {
            // Ordinary Error path: no host start, no monitoring, no fatal MessageBox.
            _tray?.SetStatus(GuardianStatus.Error);
            _tray?.SetNotice(RefusedTrayNotice);
        }
        else
        {
            var vm = _host!.Services.GetRequiredService<MainViewModel>();
            vm.ApplyInitializationStatus(initStatus);
            if (initStatus == ConfigurationInitializationStatus.ResetNoticePending)
            {
                _tray?.SetNotice(ResetTrayNotice);
            }

            // Resolve the VM (singleton) before starting the host so its status/log
            // subscriptions exist before the orchestrator's first Report().
            StartHostAsync();
        }

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
                // Silent failure would leave a working-looking UI with no monitoring.
                _ = Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        MessageBox.Show(
                            $"Background services failed to start: {ex.Message}\nMonitoring is unavailable.",
                            $"{AppEdition.Name} - Host Error",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                    }
                    catch { }
                });
            }
        });
    }

    private void ShowMainWindow()
    {
        try
        {
            if (_mainWindow == null || !_mainWindow.IsLoaded)
            {
                // Registered transient: a previously closed window cannot be re-shown,
                // so a fresh instance is resolved after the old one actually closed.
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
                    $"{AppEdition.Name} - Error",
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
        // A closed WPF Window can never be shown again; the registration is transient,
        // so the next ShowMainWindow creates a fresh instance.
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
                $"{AppEdition.Name} - Fatal Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch { }
        try
        {
            // Best-effort: remove the tray icon so no ghost icon lingers after exit.
            _tray?.Dispose();
        }
        catch { }
        Environment.Exit(1);
    }

    private bool _uiErrorReported;

    // Policy: swallow UI exceptions to keep the app running; the first one is also
    // surfaced to the user (crash.log always records them).
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        CrashLog.Append("crash.log", $"[{DateTime.Now}] UI FATAL: {e.Exception}\n");
        if (_uiErrorReported)
            return;
        _uiErrorReported = true;
        try
        {
            MessageBox.Show(
                $"An unexpected error occurred and was logged:\n{e.Exception.Message}\nThe application will keep running.",
                $"{AppEdition.Name} - Error",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch { }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved();
        CrashLog.Append("crash.log", $"[{DateTime.Now}] TASK FATAL: {e.Exception}\n");
    }
}
