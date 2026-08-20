using ProcessGuardian.Core;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ProcessGuardian.Services
{
    public class ProcessGuardianController : IProcessGuardianController, IDisposable
    {
        private readonly IConfigService _configService;
        private readonly IRingLogger _logger;
        private readonly AppState _state;
        private readonly IProcessManager _processManager;
        private readonly ITimeProvider _timeProvider;

        private readonly SemaphoreSlim _lifecycleLock = new(1,1);
        private readonly SemaphoreSlim _restartLock = new(1,1);

        private CancellationTokenSource? _cts;
        private Task? _monitorTask;

        public ProcessGuardianController(IConfigService configService, IRingLogger logger, AppState state, IProcessManager processManager, ITimeProvider timeProvider)
        {
            _configService = configService ?? throw new ArgumentNullException(nameof(configService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _state = state ?? throw new ArgumentNullException(nameof(state));
            _processManager = processManager ?? throw new ArgumentNullException(nameof(processManager));
            _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        }

        public bool IsRunning => _monitorTask != null && !_monitorTask.IsCompleted;

        public async Task StartAsync(CancellationToken token = default)
        {
            await _lifecycleLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (IsRunning)
                {
                    _logger.Log("ProcessGuardianController: StartAsync called but already running.");
                    return;
                }

                _cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                _state.IsMonitoringActive = true;
                _state.CurrentStatus = GuardianStatus.WaitingInitialDelay;
                _logger.Log("ProcessGuardianController: Starting monitoring.");

                var settings = _state.Settings;
                if (settings.InitialDelaySeconds > 0)
                {
                    _logger.Log($"ProcessGuardianController: Initial delay {settings.InitialDelaySeconds}s");
                    try
                    {
                        await _timeProvider.Delay(TimeSpan.FromSeconds(settings.InitialDelaySeconds), _cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.Log("ProcessGuardianController: Initial delay cancelled.");
                        return;
                    }
                }

                _monitorTask = Task.Run(() => MonitorLoopAsync(_cts.Token));
                // Give the monitor task a chance to schedule before returning to caller to improve test determinism
                await Task.Yield();
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }

        public async Task StopAsync()
        {
            await _lifecycleLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_cts == null)
                {
                    _logger.Log("ProcessGuardianController: StopAsync called but not running.");
                    return;
                }

                _logger.Log("ProcessGuardianController: Stopping monitoring.");
                _cts.Cancel();
                try
                {
                    if (_monitorTask != null)
                        await _monitorTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _logger.Log("ProcessGuardianController: Monitor task canceled.");
                }
                finally
                {
                    _cts.Dispose();
                    _cts = null;
                    _monitorTask = null;
                    _state.IsMonitoringActive = false;
                    _state.CurrentStatus = GuardianStatus.Stopped;
                }
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }

        private async Task MonitorLoopAsync(CancellationToken ct)
        {
            _logger.Log("ProcessGuardianController: Monitor loop started.");
            _state.CurrentStatus = GuardianStatus.Monitoring;

            var settings = _state.Settings;
            var interval = settings.CheckIntervalSeconds > 0 ? TimeSpan.FromSeconds(settings.CheckIntervalSeconds) : TimeSpan.FromSeconds(5);

            try
            {
                // Use a fixed-rate scheduling anchored to the initial loop start to avoid drift.
                var nextTick = _timeProvider.UtcNow;
                while (!ct.IsCancellationRequested)
                {
                    var now = _timeProvider.UtcNow;
                    if (now < nextTick)
                    {
                        try
                        {
                            await _timeProvider.Delay(nextTick - now, ct).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            _logger.Log("ProcessGuardianController: Monitor loop delay canceled.");
                            break;
                        }
                    }

                    _state.LastCheckTime = _timeProvider.UtcNow;
                    try
                    {
                        await CheckOnceAsync(ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.Log("ProcessGuardianController: CheckOnce cancelled.");
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError("ProcessGuardianController: Unexpected error in monitoring loop", ex);
                    }

                    // schedule next tick
                    nextTick = nextTick + interval;
                }
            }
            catch (OperationCanceledException)
            {
                _logger.Log("ProcessGuardianController: Monitor loop canceled.");
            }
            finally
            {
                _state.CurrentStatus = GuardianStatus.Stopped;
                _state.IsMonitoringActive = false;
                _logger.Log("ProcessGuardianController: Monitor loop ended.");
            }
        }

        private async Task CheckOnceAsync(CancellationToken ct)
        {
            var settings = _state.Settings;
            var targetPath = settings.TargetProcessPath;
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                _logger.Log("ProcessGuardianController: No target configured.");
                _state.CurrentStatus = GuardianStatus.Stopped;
                return;
            }

            var processName = !string.IsNullOrWhiteSpace(settings.TargetProcessName) ? settings.TargetProcessName : Path.GetFileNameWithoutExtension(targetPath);

            var candidates = await _processManager.GetProcessesByNameAsync(processName, ct).ConfigureAwait(false);

            var inspection = ProcessIdentity.Inspect(candidates, targetPath);

            switch (inspection)
            {
                case ProcessInspectionOutcome.TargetFound:
                    _logger.Log("ProcessGuardianController: Target process detected by full path.");
                    _state.CurrentRestartAttempts = 0;
                    _state.CurrentStatus = GuardianStatus.Monitoring;
                    _state.LastErrorMessage = null;
                    return;

                case ProcessInspectionOutcome.ProcessInformationUnavailable:
                    _logger.Log("ProcessGuardianController: Process information unavailable for some candidates — aborting restart to avoid unsafe start.");
                    _state.CurrentStatus = GuardianStatus.Monitoring;
                    _state.LastErrorMessage = "Process information unavailable";
                    return;

                case ProcessInspectionOutcome.SameNameDifferentPath:
                    _logger.Log("ProcessGuardianController: Same-name process found in different path; target not running.");
                    // treat as not found but do not consider info unavailable
                    break;

                case ProcessInspectionOutcome.TargetNotFound:
                default:
                    _logger.Log("ProcessGuardianController: Target process not found.");
                    break;
            }

            // At this point target not found. Attempt restart sequence if allowed.
            if (settings.MaxRestartAttempts == 0)
            {
                _logger.Log("ProcessGuardianController: MaxRestartAttempts=0 — restarts disabled.");
                return;
            }

            // Ensure only one restart sequence runs at a time
            var entered = await _restartLock.WaitAsync(0).ConfigureAwait(false);
            if (!entered)
            {
                _logger.Log("ProcessGuardianController: Restart already in progress by another tick.");
                return;
            }

            try
            {
                _state.CurrentStatus = GuardianStatus.Restarting;
                await ExecuteRestartSequenceAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                _restartLock.Release();
            }
        }

        private async Task ExecuteRestartSequenceAsync(CancellationToken ct)
        {
            var settings = _state.Settings;
            int attempts = _state.CurrentRestartAttempts;

            while (!ct.IsCancellationRequested && attempts < settings.MaxRestartAttempts)
            {
                attempts++;
                _state.CurrentRestartAttempts = attempts;
                _state.LastRestartAttemptTime = _timeProvider.UtcNow;
                _logger.Log($"ProcessGuardianController: Restart attempt #{attempts}");

                var request = new ProcessStartRequest
                {
                    FileName = settings.TargetProcessPath ?? string.Empty,
                    Arguments = settings.TargetProcessArguments,
                    WorkingDirectory = !string.IsNullOrWhiteSpace(settings.TargetProcessPath) ? Path.GetDirectoryName(settings.TargetProcessPath) : string.Empty
                };

                ProcessStartResult startResult = await _processManager.StartProcessAsync(request, ct).ConfigureAwait(false);
                if (!startResult.Started)
                {
                    _logger.LogError($"ProcessGuardianController: Process.Start failed: {startResult.ErrorMessage}", null);
                }

                // verification
                _logger.Log("ProcessGuardianController: Starting startup verification.");
                var verificationSucceeded = await WaitForTargetToAppearAsync(settings.StartupVerificationTimeoutSeconds, ct).ConfigureAwait(false);
                if (verificationSucceeded)
                {
                    _logger.Log("ProcessGuardianController: Startup verification succeeded.");
                    _state.CurrentRestartAttempts = 0;
                    _state.LastErrorMessage = null;
                    _state.CurrentStatus = GuardianStatus.Monitoring;
                    return;
                }

                _logger.Log($"ProcessGuardianController: Startup verification failed for attempt #{attempts}.");

                if (attempts < settings.MaxRestartAttempts)
                {
                    try
                    {
                        await _timeProvider.Delay(TimeSpan.FromSeconds(settings.RestartDelaySeconds), ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.Log("ProcessGuardianController: Restart delay canceled.");
                        return;
                    }
                }
            }

            if (attempts >= settings.MaxRestartAttempts)
            {
                _logger.Log("ProcessGuardianController: Restart attempts exhausted. Entering cooldown.");
                _state.CurrentStatus = GuardianStatus.Error;
                _state.LastErrorMessage = "Restart attempts exhausted";

                try
                {
                    await _timeProvider.Delay(TimeSpan.FromSeconds(settings.FailureCooldownSeconds), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _logger.Log("ProcessGuardianController: Cooldown canceled.");
                }

                _state.CurrentRestartAttempts = 0;
                _state.LastErrorMessage = null;
                _state.CurrentStatus = GuardianStatus.Monitoring;
                _logger.Log("ProcessGuardianController: Cooldown finished. Resuming monitoring.");
            }
        }

        private async Task<bool> WaitForTargetToAppearAsync(int timeoutSeconds, CancellationToken ct)
        {
            if (timeoutSeconds <= 0)
                return false;

            var settings = _state.Settings;
            var targetPath = settings.TargetProcessPath ?? string.Empty;
            var processName = !string.IsNullOrWhiteSpace(settings.TargetProcessName) ? settings.TargetProcessName : Path.GetFileNameWithoutExtension(targetPath);

            var stopAt = _timeProvider.UtcNow.AddSeconds(timeoutSeconds);
            var poll = TimeSpan.FromMilliseconds(250);

            while (_timeProvider.UtcNow <= stopAt)
            {
                ct.ThrowIfCancellationRequested();

                var candidates = await _processManager.GetProcessesByNameAsync(processName, ct).ConfigureAwait(false);
                var inspection = ProcessIdentity.Inspect(candidates, targetPath);
                if (inspection == ProcessInspectionOutcome.TargetFound)
                    return true;

                // If information unavailable, treat as failure for this attempt (but higher level avoids unsafe start)
                try
                {
                    await _timeProvider.Delay(poll, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
            }

            return false;
        }

        public void Dispose()
        {
            _cts?.Dispose();
            _lifecycleLock?.Dispose();
            _restartLock?.Dispose();
        }
    }
}
