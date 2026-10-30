using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ProcessGuardian.Domain;
using ProcessGuardian.Domain.Entities;
using ProcessGuardian.Domain.Enums;
using ProcessGuardian.Domain.Exceptions;
using ProcessGuardian.Domain.Interfaces;
using ProcessGuardian.Domain.ValueObjects;

namespace ProcessGuardian.Application.Services;

/// <summary>
/// Controlling engine for the guardian session: the single writer for monitoring
/// state and persistence (C+D). It serializes Start/Stop/Suspend/Commit internally,
/// owns a per-session <see cref="CancellationTokenSource"/>, linearizes the launch of
/// the target (the synchronous <c>Process.Start</c>) against Stop/Suspend through a
/// launch gate, computes and persists the target pin itself, merges committed UI
/// fields with controller-owned state on every save, and publishes a
/// <see cref="MonitorState"/> channel the ViewModel projects onto its bindings.
///
/// It remains a <see cref="BackgroundService"/>: the hosted instance reconciles the
/// persisted state at startup (never auto-launching a weak/unusable config) and then
/// idles; sessions are driven only through the <see cref="IMonitoringController"/>
/// contracts.
/// </summary>
public sealed class GuardianOrchestrator : BackgroundService, IMonitoringController
{
    private readonly IProcessMonitor _monitor;
    private readonly IConfigurationStore _config;
    private readonly INotificationService _notifications;
    private readonly ILogger<GuardianOrchestrator> _logger;
    private readonly IStatusReporter _status;
    private readonly ITargetIntegrityService _integrity;
    private readonly IAutostartService _autostart;
    private readonly TimeProvider _time;

    // Degraded-indicator threshold (E): the persistent IsDegraded indicator turns on only
    // when elapsed time >= T AND >= N consecutive degraded checks (poll-bound evaluation).
    private static readonly TimeSpan DegradedRequiredDuration = TimeSpan.FromSeconds(120);
    private const int DegradedRequiredConsecutive = 2;

    // Serializes every controller entry point; the UI busy gate is no longer the only guard.
    private readonly SemaphoreSlim _operationLock = new(1, 1);

    // Linearizes Process.Start against Stop/Suspend: the launch loop acquires it around the
    // synchronous start; Stop/Suspend acquire it to close the gate before cancelling.
    private readonly SemaphoreSlim _launchGate = new(1, 1);

    // Session state (guarded by _operationLock for writes; reads via properties).
    private CancellationTokenSource? _sessionCts;
    private Task? _sessionTask;
    private SessionSnapshot? _session;

    // Persistence state owned by the controller.
    private CommittedSettingsSnapshot _committed = DefaultCommitted();
    private string _committedTargetPath = string.Empty;

    // Absent when monitoring is stopped; an empty pin is a fail-closed state (nothing launches).
    private string _latestPin = string.Empty;

    private MonitorState _state = new(GuardianStatus.Stopped, false, null, false);

    // --- Test seams ------------------------------------------------------------
    // Pause hooks placed at the two linearization boundaries so the launch-gate race
    // tests are deterministic: before entering the launch critical section, and inside
    // it. Production leaves them null.
    internal Func<SessionSnapshot, CancellationToken, Task>? BeforeLaunchGateHook { get; set; }
    internal Func<Task>? InsideLaunchGateHook { get; set; }

    public GuardianOrchestrator(
        IProcessMonitor monitor,
        IConfigurationStore config,
        INotificationService notifications,
        ILogger<GuardianOrchestrator> logger,
        IStatusReporter status,
        ITargetIntegrityService integrity,
        IAutostartService autostart,
        TimeProvider? timeProvider = null)
    {
        _monitor = monitor;
        _config = config;
        _notifications = notifications;
        _logger = logger;
        _status = status;
        _integrity = integrity;
        _autostart = autostart;
        _time = timeProvider ?? TimeProvider.System;
    }

    public MonitorState CurrentState => _state;

    public event EventHandler<MonitorState>? StateChanged;

    public async Task<AppSettings> GetCurrentSettingsAsync(CancellationToken ct = default)
        => await _config.LoadAsync(ct).ConfigureAwait(false);

    private static CommittedSettingsSnapshot DefaultCommitted() =>
        new(CheckIntervalSeconds: 5, InitialDelaySeconds: 0, MaxRestartAttempts: 5,
            RestartDelaySeconds: 10, StartupVerificationTimeoutSeconds: 5,
            AutostartEnabled: false, EnableToasts: true);

    // ---------------------------------------------------------------------
    // IMonitoringController
    // ---------------------------------------------------------------------

    public async Task ReconcileAsync(CancellationToken ct = default)
    {
        await _operationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            AppSettings doc;
            try
            {
                doc = await _config.LoadAsync(ct).ConfigureAwait(false);
            }
            catch (ConfigurationUnavailableException ex)
            {
                _logger.LogError(ex, "Configuration unavailable at startup; monitoring will not run");
                Publish(new MonitorState(GuardianStatus.Error, true, ex.Guidance ?? "Configuration is unavailable", false));
                return;
            }

            _committed = CommittedSettingsSnapshot.From(doc);
            _committedTargetPath = doc.TargetProcessPath;
            _latestPin = doc.TargetHashSha256;

            // Keeps the autostart registration in sync with the persisted flag on every
            // startup (moved with the persistence path; the ViewModel never writes it).
            await SyncAutostartAsync(_committed.AutostartEnabled, ct).ConfigureAwait(false);

            if (!doc.IsMonitoringEnabled)
            {
                Publish(new MonitorState(GuardianStatus.Stopped, false, null, false));
                return;
            }

            // Persisted enabled=true: reconcile and run the pre-launch checks. A weak,
            // unusable or unverifiable config must never auto-launch; surface Error +
            // guidance (independent of EnableToasts) instead.
            if (string.IsNullOrWhiteSpace(doc.TargetProcessPath))
            {
                await DisablePersistedAsync("Monitoring cannot start: no target is selected (target confirmation required).", ct);
                return;
            }

            if (!ProcessTarget.TryCreate(doc.TargetProcessPath, out var target, out var pathError))
            {
                await DisablePersistedAsync($"Monitoring cannot start: {pathError}.", ct);
                return;
            }

            if (string.IsNullOrWhiteSpace(_latestPin))
            {
                if (AppEdition.IsElevated)
                {
                    // [product, explicit] Elevated: empty pin blocks automatic launch until an
                    // explicit re-pin. No silent refusal and no silent launch — Error with
                    // persistent "target confirmation required" guidance (independent of EnableToasts).
                    await DisablePersistedAsync(
                        "Monitoring cannot start: the target has no recorded pin (target confirmation required).", ct);
                }
                else
                {
                    // [preserve] Regular edition: an empty legacy pin keeps the compatibility
                    // skip — monitoring may resume without a pin (VerifyTargetHashAsync skips the
                    // hash for an empty pin). No pin is ever created automatically at boot.
                    _logger.LogInformation("Regular edition: no recorded pin; resuming with the compatibility skip");
                    BeginSession(CreateSessionSnapshot(target.FullPath, string.Empty, target));
                    Publish(new MonitorState(GuardianStatus.Waiting, false, null, false));
                }
                return;
            }

            var session = CreateSessionSnapshot(target.FullPath, _latestPin, target);
            BeginSession(session);
            Publish(new MonitorState(GuardianStatus.Waiting, false, null, false));
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<bool> CommitSettingsAsync(
        CommittedSettingsSnapshot snapshot, string? targetPath, CancellationToken ct = default)
    {
        await _operationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var sessionActive = SessionActive;
            _committed = snapshot;

            // Path change is effective only in the stopped state; a stale UI save during an
            // active session can never retarget a running session (controller rejects it).
            var newTarget = string.IsNullOrWhiteSpace(targetPath) ? string.Empty : targetPath;
            var pathChanged = !string.Equals(newTarget, _committedTargetPath, StringComparison.OrdinalIgnoreCase);
            if (!sessionActive && pathChanged)
            {
                _committedTargetPath = newTarget;
                _latestPin = string.Empty; // a committed path change clears the pin through the controller
            }

            var saved = await PersistEnabledAsync(enabled: sessionActive, ct).ConfigureAwait(false);
            if (!sessionActive && saved)
                Publish(new MonitorState(GuardianStatus.Stopped, false, null, false));

            await SyncAutostartAsync(snapshot.AutostartEnabled, ct).ConfigureAwait(false);
            return saved;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<StartResult> StartAsync(StartRequest request, CancellationToken ct = default)
    {
        await _operationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (SessionActive)
                return new StartResult(false, "monitoring is already running");

            // 1) Validate the path form and require an existing readable file.
            if (!ProcessTarget.TryCreate(request.TargetProcessPath, out var target, out var pathError))
            {
                _logger.LogWarning("Start rejected: {Reason}", pathError);
                return new StartResult(false, pathError);
            }

            var canonicalPath = target.FullPath;

            // 2) The pin is computed by the controller (K), never supplied by the VM.
            var hash = await _integrity.ComputeHashAsync(canonicalPath, ct).ConfigureAwait(false);
            if (!hash.IsComputed)
            {
                // A missing or unreadable target at Start rejects the request: no session.
                // (Absence of the file is allowed only inside an already-running session, where
                // the transient-missing rule applies.)
                _logger.LogWarning("Start rejected: target {Path} {Reason}", canonicalPath,
                    hash.Status == TargetHashStatus.Missing ? "is missing" : "could not be read");
                return new StartResult(false, "the target file could not be hashed; monitoring not started");
            }

            var pin = hash.Hash!;

            // 3) Persist canonical path + pin + enabled together BEFORE the session starts.
            // A failed save does not start monitoring: no session, no loop.
            _committed = request.Settings;
            _committedTargetPath = canonicalPath;
            _latestPin = pin;
            if (!await PersistEnabledAsync(enabled: true, ct).ConfigureAwait(false))
            {
                Publish(new MonitorState(GuardianStatus.Stopped, true, "settings could not be persisted; monitoring not started", true));
                return new StartResult(false, "settings could not be persisted; monitoring not started");
            }

            // 4) take the session snapshot; 5) begin the check loop.
            BeginSession(CreateSessionSnapshot(canonicalPath, pin, target));
            Publish(new MonitorState(GuardianStatus.Waiting, false, null, false));
            return new StartResult(true, null);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<StopResult> StopAsync(
        CommittedSettingsSnapshot? latestCommittedSettings = null, CancellationToken ct = default)
    {
        await _operationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (latestCommittedSettings is not null)
                _committed = latestCommittedSettings;

            var cts = _sessionCts;
            var task = _sessionTask;
            if (cts is null || task is { IsCompleted: true })
            {
                // No live session: persist the desired off-state (best-effort) and report stopped.
                var saved = await PersistEnabledAsync(enabled: false, ct).ConfigureAwait(false);
                Publish(new MonitorState(GuardianStatus.Stopped, !saved, null, !saved));
                return saved ? StopResult.AlreadyStopped() : StopResult.RuntimeStopped_SaveFailed(new Exception("persisting the disabled state failed"));
            }

            // Close the launch gate, then cancel: no Process.Start may begin after this point,
            // and none may occur after this Stop returns. A launch critical section already in
            // flight holds the gate; this wait makes RuntimeStopped honest.
            await _launchGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                _sessionCts?.Cancel();
            }
            finally
            {
                // Release only after the session settled, so nothing re-enters the gate.
                try
                {
                    await (task!).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Session task faulted during Stop");
                }

                _launchGate.Release();
            }

            _session = null;
            _sessionCts?.Dispose();
            _sessionCts = null;
            _sessionTask = null;

            // One final store save merging committed fields with controller-owned state.
            var settingsSaved = await PersistEnabledAsync(enabled: false, ct).ConfigureAwait(false);
            Publish(new MonitorState(GuardianStatus.Stopped, !settingsSaved, null, !settingsSaved));
            if (!settingsSaved)
            {
                _logger.LogError("Stop persisted runtime stop but failed to save enabled=false; disk may retain true");
                return StopResult.RuntimeStopped_SaveFailed(new Exception("final settings save failed"));
            }

            return StopResult.Succeeded();
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task SuspendAsync(CancellationToken ct = default)
    {
        await _operationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var cts = _sessionCts;
            var task = _sessionTask;
            if (cts is null || task is { IsCompleted: true })
            {
                await _launchGate.WaitAsync(ct).ConfigureAwait(false);
                try { /* nothing to cancel */ }
                finally { _launchGate.Release(); }
                return;
            }

            // Synchronously close the launch gate before the first await of the session.
            await _launchGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                _sessionCts?.Cancel();
                try
                {
                    await (task!).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Session task faulted during Suspend");
                }
            }
            finally
            {
                _launchGate.Release();
            }

            _session = null;
            _sessionCts?.Dispose();
            _sessionCts = null;
            _sessionTask = null;

            // No enabled-flag or UI-snapshot persistence.
            Publish(new MonitorState(GuardianStatus.Stopped, false, null, false));
        }
        finally
        {
            _operationLock.Release();
        }
    }

    // ---------------------------------------------------------------------
    // BackgroundService
    // ---------------------------------------------------------------------

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Reconcile persisted state once at startup, then idle. Sessions are driven
        // exclusively through the controller contracts; monitor orchestration never
        // runs its own enabling loop.
        await ReconcileAsync(stoppingToken).ConfigureAwait(false);
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal host shutdown.
        }
    }

    // ---------------------------------------------------------------------
    // Session lifecycle
    // ---------------------------------------------------------------------

    private bool SessionActive => _sessionCts is not null && _sessionTask is { IsCompleted: false };

    private void BeginSession(SessionSnapshot session)
    {
        _sessionCts = new CancellationTokenSource();
        _session = session;
        var cts = _sessionCts;
        _sessionTask = Task.Run(() => RunSessionAsync(session, cts.Token));
    }

    private async Task RunSessionAsync(SessionSnapshot session, CancellationToken ct)
    {
        var counters = new SessionCounters();
        // Path form is fixed for the whole session (validated at Start/Reconcile); the monitor
        // checks existence/readability at runtime per E — the loop never re-runs form validation.
        var target = new ProcessTarget(session.TargetProcessPath, session.TargetNameWithoutExtension, session.WorkingDirectory);
        try
        {
            if (_committed.InitialDelaySeconds > 0)
            {
                Publish(new MonitorState(GuardianStatus.Waiting, false, null, false));
                if (!await WaitForDelayAsync(TimeSpan.FromSeconds(_committed.InitialDelaySeconds), ct).ConfigureAwait(false))
                    return;
            }

            while (!ct.IsCancellationRequested)
            {
                var knobs = _committed; // numeric knobs re-read each cycle; path+pin stay fixed

                try
                {
                    var result = await _monitor.CheckAsync(target, ct).ConfigureAwait(false);
                    if (result.Outcome == ProcessOutcome.Running)
                    {
                        counters.RestartAttempts = 0;
                        counters.InitialLaunchPending = false;
                        ResetDegraded(counters);
                        Publish(new MonitorState(GuardianStatus.Monitoring, false, null, false));
                    }
                    else if (result.Outcome == ProcessOutcome.Unknown)
                    {
                        // Prolonged-Unknown discipline (E): each Unknown cycle skips silently and
                        // feeds the degraded indicator (threshold-driven, never Error).
                        _logger.LogWarning("Process state for {Target} could not be determined; skipping this cycle",
                            target.NameWithoutExtension);
                        MarkDegraded(counters);
                    }
                    else
                    {
                        var outcome = await HandleMissingAsync(session, target, counters, knobs, ct)
                            .ConfigureAwait(false);
                        if (outcome == SessionAction.EndSession)
                            return;
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // F (SPEC §7.2.1/EH-02): a throwing check behaves like Unknown — the session
                    // stays alive and only feeds the degraded indicator; it never escalates to
                    // Error on its own. The loop simply keeps checking.
                    _logger.LogWarning(ex, "CheckAsync failed for {Target}; treating the cycle as unknown", target.NameWithoutExtension);
                    MarkDegraded(counters);
                }

                if (!await WaitForNextCheckAsync(TimeSpan.FromSeconds(knobs.CheckIntervalSeconds), ct).ConfigureAwait(false))
                    return;
            }
        }
        finally
        {
            // Only the session task clears the session; it is the source of truth for "session over".
            _session = null;
        }
    }

    private enum SessionAction { Continue, EndSession }

    private sealed class SessionCounters
    {
        public int RestartAttempts;
        public bool InitialLaunchPending = true;

        // Degraded-indicator tracking (E): consecutive degraded checks and the monotonic
        // timestamp of the first one in the current run. -1 = not yet set (a real GetTimestamp
        // may legitimately be 0, e.g. a manual clock starting at the epoch).
        public int ConsecutiveDegraded;
        public long FirstDegradedTimestamp = -1;
    }

    private void MarkDegraded(SessionCounters counters)
    {
        if (counters.ConsecutiveDegraded == 0)
            counters.FirstDegradedTimestamp = _time.GetTimestamp();
        counters.ConsecutiveDegraded++;
        PublishDegradedIfThreshold(counters);
    }

    private void ResetDegraded(SessionCounters counters)
    {
        counters.ConsecutiveDegraded = 0;
        counters.FirstDegradedTimestamp = -1;
        if (CurrentState.IsDegraded)
            Publish(new MonitorState(CurrentState.Status, false, null, CurrentState.SaveFailed));
    }

    // Time-based threshold, evaluated on the next poll (E): the persistent indicator turns on
    // only after >= N consecutive degraded checks AND >= T elapsed. Below threshold the session
    // keeps its normal status (IsDegraded=false) — a single glitch never flags.
    private void PublishDegradedIfThreshold(SessionCounters counters)
    {
        var enoughChecks = counters.ConsecutiveDegraded >= DegradedRequiredConsecutive;
        var enoughTime = counters.FirstDegradedTimestamp >= 0
            && _time.GetElapsedTime(counters.FirstDegradedTimestamp) >= DegradedRequiredDuration;
        if (enoughChecks && enoughTime && !CurrentState.IsDegraded)
            Publish(new MonitorState(CurrentState.Status, true, "the target identity or reachability cannot be verified", CurrentState.SaveFailed));
    }

    private async Task<SessionAction> HandleMissingAsync(
        SessionSnapshot session, ProcessTarget target,
        SessionCounters counters,
        CommittedSettingsSnapshot knobs, CancellationToken ct)
    {
        if (counters.InitialLaunchPending)
        {
            _logger.LogInformation("Initial start for {Target}", target.NameWithoutExtension);
            var outcome = await StartAndVerifyAsync(target, knobs, showRecoveryToast: false, ct).ConfigureAwait(false);
            if (outcome == StartOutcome.SessionEnded || outcome == StartOutcome.Blocked)
                return SessionAction.EndSession;
            // A degraded (missing/unreadable) free launch spends no budget, does not consume the
            // free launch, and feeds the degraded indicator (E): keep initial-pending, mark degraded.
            if (outcome == StartOutcome.Degraded)
            {
                MarkDegraded(counters);
                return SessionAction.Continue;
            }
            counters.InitialLaunchPending = false;
            if (outcome == StartOutcome.Verified)
                ResetDegraded(counters);
            return SessionAction.Continue;
        }

        if (knobs.MaxRestartAttempts > 0 && counters.RestartAttempts >= knobs.MaxRestartAttempts)
        {
            _logger.LogError("Max restart attempts ({Max}) reached for {Target}", knobs.MaxRestartAttempts, target.NameWithoutExtension);
            try
            {
                if (knobs.EnableToasts)
                    await _notifications.ShowAsync("Recovery Failed", string.Empty, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to show exhaustion toast");
            }

            await TryPersistDisabledBestEffortAsync(knobs).ConfigureAwait(false);
            Publish(new MonitorState(GuardianStatus.Error, false,
                "monitoring stopped after repeated failed recovery attempts", _lastPersistFailed));
            return SessionAction.EndSession;
        }

        Publish(new MonitorState(GuardianStatus.Restarting, false, null, false));
        _logger.LogInformation("Restart attempt for {Target}", target.NameWithoutExtension);

        var started = await StartAndVerifyAsync(target, knobs, showRecoveryToast: true, ct).ConfigureAwait(false);
        if (started == StartOutcome.SessionEnded || started == StartOutcome.Blocked)
            return SessionAction.EndSession;
        if (started == StartOutcome.Degraded)
        {
            // A degraded (missing/unreadable) relaunch spends no budget and sleeps no restart
            // delay; the session keeps polling and feeds the degraded indicator. Only a genuine
            // launch that ran and failed advances the consecutive counter (budget-charge rule, E).
            MarkDegraded(counters);
            return SessionAction.Continue;
        }

        // Budget-charge rule (E): only a genuine launched-but-failed attempt advances the
        // consecutive counter; a successful automatic relaunch counts only toward the window
        // (I, not in Stage 1) and is reset when the target is observed Running. So Verified
        // must not consume the consecutive budget.
        if (started == StartOutcome.NotStarted || started == StartOutcome.VerifyFailed)
            counters.RestartAttempts++;
        if (started == StartOutcome.Verified)
            ResetDegraded(counters); // a genuinely-restarted target clears the degraded indicator
        var hasMoreAttempts = counters.RestartAttempts < knobs.MaxRestartAttempts || knobs.MaxRestartAttempts == 0;
        if (started == StartOutcome.NotStarted || (started == StartOutcome.VerifyFailed && hasMoreAttempts))
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(knobs.RestartDelaySeconds), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return SessionAction.EndSession;
            }
        }

        return SessionAction.Continue;
    }

    private enum StartOutcome { NotStarted, Verified, VerifyFailed, Blocked, Degraded, SessionEnded }

    private async Task<StartOutcome> StartAndVerifyAsync(
        ProcessTarget target, CommittedSettingsSnapshot knobs, bool showRecoveryToast, CancellationToken ct)
    {
        var hash = await VerifyTargetHashAsync(target, knobs, ct).ConfigureAwait(false);
        switch (hash)
        {
            case HashVerifyResult.Cancelled:
                return StartOutcome.SessionEnded;
            case HashVerifyResult.Missing:
                _logger.LogWarning("Target {Target} is missing before launch; skipping (degraded), no budget spent",
                    target.NameWithoutExtension);
                return StartOutcome.Degraded;
            case HashVerifyResult.ReadError:
                _logger.LogWarning("Target {Target} could not be read before launch; skipping (degraded), no budget spent",
                    target.NameWithoutExtension);
                return StartOutcome.Degraded;
            case HashVerifyResult.Mismatch:
                // Fail closed (K keeping today's behavior until F): persist disabled, terminate the
                // session, publish Error — even if that save fails, with the save-failure flag surfaced.
                await TryPersistDisabledBestEffortAsync(knobs).ConfigureAwait(false);
                Publish(new MonitorState(GuardianStatus.Error, false,
                    "the target binary no longer matches the recorded pin; monitoring stopped", _lastPersistFailed));
                return StartOutcome.SessionEnded;
        }

        // Linearized launch: the last session/gate check and the synchronous Process.Start
        // execute in one non-awaiting critical section. Stop/Suspend close this gate and
        // cancel the session before awaiting it, so no launch begins after they act and
        // none begins after a completed Stop returns.
        if (BeforeLaunchGateHook is not null)
            await BeforeLaunchGateHook(CurrentSessionOr(target), ct).ConfigureAwait(false);

        await _launchGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check session identity under the gate; no await may precede the launch itself.
            if (ct.IsCancellationRequested)
                return StartOutcome.SessionEnded;

            if (InsideLaunchGateHook is not null)
                await InsideLaunchGateHook().ConfigureAwait(false);

            bool started;
            try
            {
                started = await _monitor.StartAsync(target, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return StartOutcome.SessionEnded;
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

            var verified = await VerifyStartupSafeAsync(target, knobs, ct).ConfigureAwait(false);
            if (verified == VerifyOutcome.Verified)
            {
                _logger.LogInformation("Process {Target} verified running", target.NameWithoutExtension);
                Publish(new MonitorState(GuardianStatus.Monitoring, false, null, false));
                if (showRecoveryToast)
                {
                    try
                    {
                        if (knobs.EnableToasts)
                            await _notifications.ShowAsync("Process Recovered", string.Empty, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to show recovery toast");
                    }
                }
                return StartOutcome.Verified;
            }

            if (verified == VerifyOutcome.Failed)
            {
                _logger.LogWarning("Startup verification failed for {Target}", target.NameWithoutExtension);
                return StartOutcome.VerifyFailed;
            }

            // VerifyStartupAsync observed cancellation: the session is ending.
            return StartOutcome.SessionEnded;
        }
        finally
        {
            _launchGate.Release();
        }
    }

    private enum VerifyOutcome { Verified, Failed, Cancelled }

    private async Task<VerifyOutcome> VerifyStartupSafeAsync(ProcessTarget target, CommittedSettingsSnapshot knobs, CancellationToken ct)
    {
        try
        {
            var verified = await _monitor.VerifyStartupAsync(
                target, TimeSpan.FromSeconds(knobs.StartupVerificationTimeoutSeconds), ct).ConfigureAwait(false);
            return verified ? VerifyOutcome.Verified : VerifyOutcome.Failed;
        }
        catch (OperationCanceledException)
        {
            return VerifyOutcome.Cancelled;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "VerifyStartupAsync threw for {Target}", target.NameWithoutExtension);
            return VerifyOutcome.Failed;
        }
    }

    private SessionSnapshot CurrentSessionOr(ProcessTarget target) =>
        _session ?? CreateSessionSnapshot(target.FullPath, _latestPin, target);

    // Empty pin: the regular-edition compatibility skip verifies nothing (a session pin is
    // only empty in the regular edition; elevated reconciliation refuses empty-pin
    // auto-resume). A non-empty saved pin is always verified in both editions.
    private async Task<HashVerifyResult> VerifyTargetHashAsync(ProcessTarget target, CommittedSettingsSnapshot knobs, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_latestPin))
        {
            _logger.LogInformation("Target {Target} has no recorded pin; integrity check skipped", target.NameWithoutExtension);
            return HashVerifyResult.Verified;
        }

        TargetHashResult read;
        try
        {
            read = await _integrity.ComputeHashAsync(target.FullPath, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return HashVerifyResult.Cancelled;
        }

        // K distinguishes a clean "no file" and a permission/IO failure from a mismatch; only a
        // successfully-read differing hash is Mismatch.
        if (read.Status == TargetHashStatus.Missing)
            return HashVerifyResult.Missing;
        if (read.Status == TargetHashStatus.ReadError)
            return HashVerifyResult.ReadError;

        if (string.Equals(read.Hash, _latestPin, StringComparison.OrdinalIgnoreCase))
            return HashVerifyResult.Verified;

        _logger.LogError(
            "Target binary hash mismatch for {Target} (expected {Expected}, got {Actual}); launch blocked",
            target.NameWithoutExtension, _latestPin, read.Hash);
        try
        {
            if (knobs.EnableToasts)
                await _notifications.ShowAsync("Target Changed", string.Empty, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to show target-changed toast");
        }

        return HashVerifyResult.Mismatch;
    }

    // Fatal cause: the caller exits the cycle loop, then persists enabled=false best-effort
    // (TryPersistDisabledBestEffortAsync) and reports Error even if that save fails (SaveFailed
    // is surfaced separately) — each fatal branch inlines these two steps.

    // ---------------------------------------------------------------------
    // Persistence and autostart
    // ---------------------------------------------------------------------

    private bool _lastPersistFailed;

    private async Task<bool> PersistEnabledAsync(bool enabled, CancellationToken ct)
    {
        try
        {
            var doc = await _config.LoadAsync(ct).ConfigureAwait(false);
            ApplyCommitted(doc, _committed);
            doc.TargetProcessPath = _committedTargetPath;
            doc.TargetHashSha256 = _latestPin;
            doc.IsMonitoringEnabled = enabled;
            await _config.SaveAsync(doc, ct).ConfigureAwait(false);
            _lastPersistFailed = false;
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _lastPersistFailed = true;
            _logger.LogError(ex, "Persist settings (enabled={Enabled}) failed", enabled);
            return false;
        }
    }

    private async Task TryPersistDisabledBestEffortAsync(CommittedSettingsSnapshot knobs)
    {
        _ = knobs;
        try
        {
            var doc = await _config.LoadAsync(CancellationToken.None).ConfigureAwait(false);
            ApplyCommitted(doc, _committed);
            doc.TargetProcessPath = _committedTargetPath;
            doc.TargetHashSha256 = _latestPin;
            doc.IsMonitoringEnabled = false;
            await _config.SaveAsync(doc, CancellationToken.None).ConfigureAwait(false);
            _lastPersistFailed = false;
        }
        catch (Exception ex)
        {
            _lastPersistFailed = true;
            _logger.LogError(ex, "Failed to persist monitoring disabled after a fatal cause");
        }
    }

    private async Task DisablePersistedAsync(string reason, CancellationToken ct)
    {
        await TryPersistDisabledBestEffortAsync(_committed).ConfigureAwait(false);
        var saveFailed = _lastPersistFailed;
        Publish(new MonitorState(GuardianStatus.Error, true, reason, saveFailed));
    }

    private static void ApplyCommitted(AppSettings doc, CommittedSettingsSnapshot s)
    {
        doc.CheckIntervalSeconds = s.CheckIntervalSeconds;
        doc.InitialDelaySeconds = s.InitialDelaySeconds;
        doc.MaxRestartAttempts = s.MaxRestartAttempts;
        doc.RestartDelaySeconds = s.RestartDelaySeconds;
        doc.StartupVerificationTimeoutSeconds = s.StartupVerificationTimeoutSeconds;
        doc.AutostartEnabled = s.AutostartEnabled;
        doc.EnableToasts = s.EnableToasts;
    }

    private async Task SyncAutostartAsync(bool desiredEnabled, CancellationToken ct)
    {
        try
        {
            if (desiredEnabled != await _autostart.IsEnabledAsync(ct).ConfigureAwait(false))
                await _autostart.SetEnabledAsync(desiredEnabled, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Autostart sync failed");
        }
    }

    private static SessionSnapshot CreateSessionSnapshot(string canonicalPath, string pin, ProcessTarget target) =>
        new(canonicalPath, target.NameWithoutExtension, target.WorkingDirectory, pin);

    // ---------------------------------------------------------------------
    // Waits
    // ---------------------------------------------------------------------

    private async Task<bool> WaitForDelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
            return !ct.IsCancellationRequested;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    // Sliced 250 ms waits (Stop stays responsive); returns false when the host/session is
    // cancelled or the persistence state says the session should end.
    private async Task<bool> WaitForNextCheckAsync(TimeSpan interval, CancellationToken ct)
    {
        const int sliceMs = 250;
        var remaining = interval.TotalMilliseconds >= int.MaxValue
            ? int.MaxValue
            : (int)interval.TotalMilliseconds;
        while (remaining > 0)
        {
            var slice = Math.Min(sliceMs, remaining);
            try
            {
                await Task.Delay(slice, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            remaining -= slice;
        }
        return !ct.IsCancellationRequested;
    }

    // ---------------------------------------------------------------------
    // Monitoring state channel
    // ---------------------------------------------------------------------

    private void Publish(MonitorState state)
    {
        if (_state == state)
            return;
        _state = state;
        _status.Report(state.Status); // tray/tooltip compatibility channel
        try
        {
            StateChanged?.Invoke(this, state);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "StateChanged subscriber threw");
        }
    }
}
