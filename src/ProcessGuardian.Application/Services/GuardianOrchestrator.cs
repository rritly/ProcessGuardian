using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ProcessGuardian.Domain.Entities;
using ProcessGuardian.Domain.Enums;
using ProcessGuardian.Domain.Interfaces;
using ProcessGuardian.Domain.ValueObjects;

namespace ProcessGuardian.Application.Services;

public sealed class GuardianOrchestrator : BackgroundService
{
    private readonly IProcessMonitor _monitor;
    private readonly IConfigurationStore _config;
    private readonly INotificationService _notifications;
    private readonly ILogger<GuardianOrchestrator> _logger;
    private readonly IStatusReporter _status;

    private GuardianStatus _currentStatus = GuardianStatus.Stopped;
    private int _restartAttempts = 0;
    private bool _initialLaunchPending;

    public GuardianOrchestrator(
        IProcessMonitor monitor,
        IConfigurationStore config,
        INotificationService notifications,
        ILogger<GuardianOrchestrator> logger,
        IStatusReporter status)
    {
        _monitor = monitor;
        _config = config;
        _notifications = notifications;
        _logger = logger;
        _status = status;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var settings = await _config.LoadAsync(stoppingToken);
            if (!settings.IsMonitoringEnabled)
            {
                if (_currentStatus != GuardianStatus.Error)
                    SetStatus(GuardianStatus.Stopped);
                try
                {
                    await WaitForNextCheckAsync(TimeSpan.FromSeconds(1), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                continue;
            }

            _logger.LogInformation("Monitoring enabled: path={Path}, flag={Enabled}",
                settings.TargetProcessPath, settings.IsMonitoringEnabled);

            try
            {
                await RunMonitoringCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Monitoring cycle crashed unexpectedly");
                SetStatus(GuardianStatus.Error);
                if (!await WaitForNextCheckAsync(TimeSpan.FromSeconds(5), stoppingToken))
                    break;
            }
        }
        SetStatus(GuardianStatus.Stopped);
    }

    private async Task RunMonitoringCycleAsync(CancellationToken ct)
    {
        var settings = await _config.LoadAsync(ct);

        // Restart budget resets once per session (user Start), not per inner iteration.
        _restartAttempts = 0;
        // The first launch of a session is free: it consumes no attempts. Granted once.
        _initialLaunchPending = true;

        if (settings.InitialDelaySeconds > 0)
        {
            SetStatus(GuardianStatus.Waiting);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(settings.InitialDelaySeconds), ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }

        while (!ct.IsCancellationRequested)
        {
            settings = await _config.LoadAsync(ct); // Reload every cycle for live updates
            if (!settings.IsMonitoringEnabled) break;

            if (!ProcessTarget.TryCreate(settings.TargetProcessPath, out var target, out var pathError))
            {
                _logger.LogWarning("Invalid target path {Path}: {Reason}", settings.TargetProcessPath, pathError);
                SetStatus(GuardianStatus.Error);
                if (!await WaitForNextCheckAsync(TimeSpan.FromSeconds(settings.CheckIntervalSeconds), ct))
                    return;
                continue;
            }

            try
            {
                var result = await _monitor.CheckAsync(target, ct);
                if (result.Outcome == ProcessOutcome.Running)
                {
                    _restartAttempts = 0;
                    _initialLaunchPending = false;
                    SetStatus(GuardianStatus.Monitoring);
                }
                else
                {
                    await HandleMissingAsync(settings, target, ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CheckAsync failed for {Target}", target.NameWithoutExtension);
                SetStatus(GuardianStatus.Error);
            }

            if (!await WaitForNextCheckAsync(TimeSpan.FromSeconds(settings.CheckIntervalSeconds), ct))
                return;
        }
    }

    private async Task HandleMissingAsync(AppSettings s, ProcessTarget target, CancellationToken ct)
    {
        if (_initialLaunchPending)
        {
            // Free initial launch: consumes no attempts and never flashes Restarting.
            _initialLaunchPending = false;
            _logger.LogInformation("Initial start for {Target}", target.NameWithoutExtension);
            await StartAndVerifyAsync(s, target, ct, showRecoveryToast: false);
            return;
        }

        if (s.MaxRestartAttempts > 0 && _restartAttempts >= s.MaxRestartAttempts)
        {
            _logger.LogError("Max restart attempts ({Max}) reached for {Target}", s.MaxRestartAttempts, target.NameWithoutExtension);
            SetStatus(GuardianStatus.Error);
            try
            {
                if (s.EnableToasts)
                    await _notifications.ShowAsync("Recovery Failed", string.Empty, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to show exhaustion toast");
            }
            _restartAttempts = 0;
            s.IsMonitoringEnabled = false;
            try
            {
                await _config.SaveAsync(s, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist monitoring disabled after exhaustion");
            }
            return;
        }

        _restartAttempts++;
        _logger.LogInformation("Restart attempt #{Attempt} for {Target}", _restartAttempts, target.NameWithoutExtension);

        var outcome = await StartAndVerifyAsync(s, target, ct, showRecoveryToast: true);
        if (outcome == StartOutcome.VerifyFailed && (_restartAttempts < s.MaxRestartAttempts || s.MaxRestartAttempts == 0))
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(s.RestartDelaySeconds), ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }
    }

    private enum StartOutcome { NotStarted, Verified, VerifyFailed }

    private async Task<StartOutcome> StartAndVerifyAsync(AppSettings s, ProcessTarget target, CancellationToken ct, bool showRecoveryToast)
    {
        bool started;
        try
        {
            started = await _monitor.StartAsync(target, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "StartAsync threw for {Target}", target.NameWithoutExtension);
            started = false;
        }

        if (!started)
        {
            _logger.LogError("Failed to start process {Target}", target.NameWithoutExtension);
            return StartOutcome.NotStarted;
        }

        try
        {
            var verified = await _monitor.VerifyStartupAsync(target, TimeSpan.FromSeconds(s.StartupVerificationTimeoutSeconds), ct);
            if (verified)
            {
                var attemptNumber = _restartAttempts; // Do NOT reset here; reset only when CheckAsync sees Running
                _logger.LogInformation("Process {Target} verified running", target.NameWithoutExtension);
                SetStatus(GuardianStatus.Monitoring);
                    if (showRecoveryToast)
                    {
                        try
                        {
                            if (s.EnableToasts)
                                await _notifications.ShowAsync("Process Recovered", string.Empty, ct);
                        }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to show recovery toast");
                    }
                }
                return StartOutcome.Verified;
            }

            _logger.LogWarning("Startup verification failed for {Target}", target.NameWithoutExtension);
            return StartOutcome.VerifyFailed;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "VerifyStartupAsync threw for {Target}", target.NameWithoutExtension);
            return StartOutcome.VerifyFailed;
        }
    }

    private void SetStatus(GuardianStatus status)
    {
        if (_currentStatus == status) return;
        _currentStatus = status;
        _status.Report(status);
    }

    // Waits in 250 ms slices so Stop and settings changes stay responsive.
    // Returns false if monitoring was disabled during the wait (caller stops the cycle).
    private async Task<bool> WaitForNextCheckAsync(TimeSpan interval, CancellationToken ct)
    {
        const int sliceMs = 250;
        // Cap the wait at int.MaxValue ms: a huge CheckIntervalSeconds overflows the
        // int cast into a negative value and would turn this loop into a hot loop.
        var remaining = interval.TotalMilliseconds >= int.MaxValue
            ? int.MaxValue
            : (int)interval.TotalMilliseconds;
        while (remaining > 0)
        {
            var slice = Math.Min(sliceMs, remaining);
            await Task.Delay(slice, ct);
            remaining -= slice;

            var settings = await _config.LoadAsync(ct);
            if (!settings.IsMonitoringEnabled)
                return false;
        }
        return true;
    }
}
