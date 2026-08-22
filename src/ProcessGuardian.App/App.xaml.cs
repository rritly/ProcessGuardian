using Microsoft.UI.Xaml;
using System;
using System.Threading;
using ProcessGuardian.Services.Logging;
using ProcessGuardian.Services;
using ProcessGuardian.Core;

namespace ProcessGuardian
{
    public partial class App : Application
    {
        private Window? _window;
        public ProcessGuardian.Core.AppState AppState { get; private set; } = new ProcessGuardian.Core.AppState();
        private IProcessGuardianController? _controller;
        private CancellationTokenSource? _appCts;
        private IFileStorage? _storage;
        private RingLogger? _concreteLogger;
        private IRingLogger? _logger;

        public App()
        {
            InitializeComponent();
        }

        protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            // Determine launch mode
            var launchArgs = args?.Arguments ?? string.Empty;
            var isBackground = ProcessGuardian.Services.LaunchArgumentParser.IsBackgroundLaunch(launchArgs);

            // Composition root: initialize storage, config service and load settings
            try
            {
                _storage = new ApplicationDataFileStorage(AppIdentity.Publisher, AppIdentity.Product);
                var configService = new ConfigService(_storage);
                var loadResult = await configService.LoadOrCreateAsync();

                // Initialize AppState with loaded or default settings
                AppState = new AppState();
                AppState.Settings = loadResult.Settings ?? configService.GetDefaults();
                if (!string.IsNullOrEmpty(loadResult.ErrorMessage))
                {
                    AppState.LastErrorMessage = loadResult.ErrorMessage;
                    AppState.CurrentStatus = GuardianStatus.Error;
                }

                // Create logger (concrete) and assign adapter to simple IRingLogger
                _concreteLogger = new RingLogger(_storage, AppState.Settings.LogBufferSize, null, AppState.Settings.EnableLogging);
                _logger = new ProcessGuardian.Services.Logging.SimpleRingLoggerAdapter(_concreteLogger);

                // Create other services
                var processManager = new SystemProcessManager();
                var timeProvider = new SystemTimeProvider();

                // Create controller (single instance)
                _controller = new ProcessGuardian.Services.ProcessGuardianController(configService, _logger, AppState, processManager, timeProvider);

                // Prepare application cancellation token
                _appCts = new CancellationTokenSource();

                // Start monitoring if enabled in settings
                if (AppState.Settings.MonitoringEnabled)
                {
                    try
                    {
                        await _controller.StartAsync(_appCts.Token);
                    }
                    catch (Exception ex)
                    {
                        // Record but do not crash
                        AppState.LastErrorMessage = ex.Message;
                        AppState.CurrentStatus = GuardianStatus.Error;
                    }
                }

                // Register shutdown handlers
                AppDomain.CurrentDomain.ProcessExit += (_, __) => ShutdownSync();
                this.UnhandledException += (_, e) => { try { ShutdownSync(); } catch { } };

                // UI: only create window when not background
                if (!isBackground)
                {
                    _window = new MainWindow();
                    _window.Closed += async (_, __) => await ShutdownAsync();
                    _window.Activate();
                }
            }
            catch (Exception ex)
            {
                // Recover: keep defaults and record diagnostics
                AppState = new AppState();
                AppState.LastErrorMessage = ex.Message;
                AppState.CurrentStatus = GuardianStatus.Error;
            }
        }

        private async System.Threading.Tasks.Task ShutdownAsync()
        {
            try
            {
                _appCts?.Cancel();
                if (_controller != null)
                {
                    try
                    {
                        await _controller.StopAsync().ConfigureAwait(false);
                    }
                    catch { }
                }

                // Flush concrete logger if available
                if (_concreteLogger != null)
                {
                    try
                    {
                        await _concreteLogger.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch { }
                }
            }
            finally
            {
                try { _appCts?.Dispose(); } catch { }
            }
        }

        private void ShutdownSync()
        {
            try
            {
                _appCts?.Cancel();
                if (_controller != null)
                {
                    try { _controller.StopAsync().GetAwaiter().GetResult(); } catch { }
                }

                if (_concreteLogger != null)
                {
                    try { _concreteLogger.FlushAsync(CancellationToken.None).GetAwaiter().GetResult(); } catch { }
                }
            }
            catch { }
            finally
            {
                try { _appCts?.Dispose(); } catch { }
            }
        }
    }
}
