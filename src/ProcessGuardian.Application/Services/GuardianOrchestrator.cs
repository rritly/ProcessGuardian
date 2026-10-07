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
    private readonly ITargetIntegrityService _integrity;

    private GuardianStatus _currentStatus = GuardianStatus.Stopped;
    private int _restartAttempts = 0;
    private bool _initialLaunchPending;

    public GuardianOrchestrator(
        IProcessMonitor monitor,
        IConfigurationStore config,
        INotificationService notifications,
        ILogger<GuardianOrchestrator> logger,
        IStatusReporter status,
        ITargetIntegrityService integrity)
    {
        _monitor = monitor;
        _config = config;
        _notifications = notifications;
        _logger = logger;
        _status = status;
        _integrity = integrity;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Every step is under the catch-all: an unexpected exception from the config
        // store or any await must degrade to Error + cooldown, never stop the host.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunLoopStepAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Monitoring loop crashed unexpectedly");
                SetStatus(GuardianStatus.Error);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        SetStatus(GuardianStatus.Stopped);
    }

    // One outer-loop step: load settings, idle while disabled, or run the cycle.
    private async Task RunLoopStepAsync(CancellationToken ct)
    {
        var settings = await _config.LoadAsync(ct).ConfigureAwait(false);
        if (!settings.IsMonitoringEnabled)
        {
            if (_currentStatus != GuardianStatus.Error)
                SetStatus(GuardianStatus.Stopped);
            await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            return;
        }

        _logger.LogInformation("Monitoring enabled: path={Path}, flag={Enabled}",
            settings.TargetProcessPath, settings.IsMonitoringEnabled);

        await RunMonitoringCycleAsync(ct).ConfigureAwait(false);
    }

    private async Task RunMonitoringCycleAsync(CancellationToken ct)
    {
        var settings = await _config.LoadAsync(ct);

        // The budget counts consecutive failed restarts; it resets when a check
        // observes the process running, and a fresh cycle (user Start) starts at 0.
        _restartAttempts = 0;
        // The first launch of a session is free: it consumes no attempts. Granted once.
        _initialLaunchPending = true;

        if (settings.InitialDelaySeconds > 0)
        {
            SetStatus(GuardianStatus.Waiting);
            // Sliced like every other wait so Stop and settings changes stay responsive
            // instead of pinning the orchestrator in Waiting for the whole delay.
            if (!await WaitForNextCheckAsync(TimeSpan.FromSeconds(settings.InitialDelaySeconds), ct).ConfigureAwait(false))
                return;
        }

        while (!ct.IsCancellationRequested)
        {
            settings = await _config.LoadAsync(ct).ConfigureAwait(false); // Reload every cycle for live updates
            if (!settings.IsMonitoringEnabled) break;

            if (!ProcessTarget.TryCreate(settings.TargetProcessPath, out var target, out var pathError))
            {
                _logger.LogWarning("Invalid target path {Path}: {Reason}", settings.TargetProcessPath, pathError);
                SetStatus(GuardianStatus.Error);
                if (!await WaitForNextCheckAsync(TimeSpan.FromSeconds(settings.CheckIntervalSeconds), ct).ConfigureAwait(false))
                    return;
                continue;
            }

            try
            {
                var result = await _monitor.CheckAsync(target, ct).ConfigureAwait(false);
                if (result.Outcome == ProcessOutcome.Running)
                {
                    _restartAttempts = 0;
                    _initialLaunchPending = false;
                    SetStatus(GuardianStatus.Monitoring);
                }
                else
                {
                    await HandleMissingAsync(settings, target, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "CheckAsync failed for {Target}", target.NameWithoutExtension);
                SetStatus(GuardianStatus.Error);
            }

            if (!await WaitForNextCheckAsync(TimeSpan.FromSeconds(settings.CheckIntervalSeconds), ct).ConfigureAwait(false))
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
            await StartAndVerifyAsync(s, target, ct, showRecoveryToast: false).ConfigureAwait(false);
            return;
        }

        if (s.MaxRestartAttempts > 0 && _restartAttempts >= s.MaxRestartAttempts)
        {
            _logger.LogError("Max restart attempts ({Max}) reached for {Target}", s.MaxRestartAttempts, target.NameWithoutExtension);
            SetStatus(GuardianStatus.Error);
            try
            {
                if (s.EnableToasts)
                    await _notifications.ShowAsync("Recovery Failed", string.Empty, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to show exhaustion toast");
            }
            _restartAttempts = 0;
            s.IsMonitoringEnabled = false;
            try
            {
                await _config.SaveAsync(s, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist monitoring disabled after exhaustion");
            }
            return;
        }

        _restartAttempts++;
        SetStatus(GuardianStatus.Restarting); // budgeted restart is user-visible, unlike the free initial launch
        _logger.LogInformation("Restart attempt #{Attempt} for {Target}", _restartAttempts, target.NameWithoutExtension);

        var outcome = await StartAndVerifyAsync(s, target, ct, showRecoveryToast: true).ConfigureAwait(false);
        if (outcome == StartOutcome.Blocked)
            return; // Monitoring was already disabled; no retry against a changed target.

        // Back off before the next attempt after a failed launch or a failed startup
        // verification. Exhaustion (the budget was just consumed) is reported on the
        // next check without an extra delay.
        var hasMoreAttempts = _restartAttempts < s.MaxRestartAttempts || s.MaxRestartAttempts == 0;
        if (outcome == StartOutcome.NotStarted || (outcome == StartOutcome.VerifyFailed && hasMoreAttempts))
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(s.RestartDelaySeconds), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }
    }

    private enum StartOutcome { NotStarted, Verified, VerifyFailed, Blocked }

    // Target-hash pin verification. An empty pin (settings written before this
    // feature, or Start never pressed) skips the check for backwards compatibility.
    // A hash read failure is treated as a mismatch: fail closed.
    private async Task<bool> VerifyTargetHashAsync(AppSettings s, ProcessTarget target, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(s.TargetHashSha256))
        {
            _logger.LogInformation("Target {Target} has no recorded hash; integrity check skipped", target.NameWithoutExtension);
            return true;
        }

        var actual = await _integrity.ComputeHashAsync(target.FullPath, ct).ConfigureAwait(false);
        if (actual is not null &&
            string.Equals(actual, s.TargetHashSha256, StringComparison.OrdinalIgnoreCase))
            return true;

        _logger.LogError(
            "Target binary hash mismatch for {Target} (expected {Expected}, got {Actual}); launch blocked",
            target.NameWithoutExtension, s.TargetHashSha256, actual ?? "<unreadable>");
        SetStatus(GuardianStatus.Error);
        try
        {
            if (s.EnableToasts)
                await _notifications.ShowAsync("Target Changed", string.Empty, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to show target-changed toast");
        }

        _restartAttempts = 0;
        s.IsMonitoringEnabled = false;
        try
        {
            await _config.SaveAsync(s, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist monitoring disabled after target change");
        }

        return false;
    }

    private async Task<StartOutcome> StartAndVerifyAsync(AppSettings s, ProcessTarget target, CancellationToken ct, bool showRecoveryToast)
    {
        if (!await VerifyTargetHashAsync(s, target, ct).ConfigureAwait(false))
            return StartOutcome.Blocked;

        bool started;
        try
        {
            started = await _monitor.StartAsync(target, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
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
            var verified = await _monitor.VerifyStartupAsync(target, TimeSpan.FromSeconds(s.StartupVerificationTimeoutSeconds), ct).ConfigureAwait(false);
            if (verified)
            {
                _logger.LogInformation("Process {Target} verified running", target.NameWithoutExtension);
                SetStatus(GuardianStatus.Monitoring);
                if (showRecoveryToast)
                {
                    try
                    {
                        if (s.EnableToasts)
                            await _notifications.ShowAsync("Process Recovered", string.Empty, ct).ConfigureAwait(false);
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
        catch (OperationCanceledException)
        {
            // Cancellation during startup verification is not a failed attempt;
            // let it propagate so the loop exits cleanly.
            throw;
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
            await Task.Delay(slice, ct).ConfigureAwait(false);
            remaining -= slice;

            var settings = await _config.LoadAsync(ct).ConfigureAwait(false);
            if (!settings.IsMonitoringEnabled)
                return false;
        }
        return true;
    }
}
