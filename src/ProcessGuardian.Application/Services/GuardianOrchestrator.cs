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
    private readonly IResetNoticeAck? _resetNoticeAck;
    private readonly TimeProvider _time;

    // Degraded-indicator threshold (E): the persistent IsDegraded indicator turns on only
    // when elapsed time >= T AND >= N consecutive degraded checks (poll-bound evaluation).
    private static readonly TimeSpan DegradedRequiredDuration = TimeSpan.FromSeconds(120);
    private const int DegradedRequiredConsecutive = 2;

    // Bounded observation cadence of the startup-appearance and stability phases (v2.8.1):
    // while a phase is active the session loop observes at this cadence — it never blocks or
    // postpones a normal CheckIntervalSeconds tick (a poll landing at/after the next tick's
    // deadline IS that normal tick). Independent of CheckIntervalSeconds, which may far exceed
    // the one-second windows. Exit notification is not used; the finite detection tolerance is
    // ~PhasePollMs plus the check duration and is documented instead of claimed exact.
    private const int PhasePollMs = 100;

    // Serializes every controller entry point; the UI busy gate is no longer the only guard.
    private readonly SemaphoreSlim _operationLock = new(1, 1);

    // Linearizes Process.Start against Stop/Suspend: the launch loop acquires it around the
    // synchronous start; Stop/Suspend acquire it to close the gate before cancelling. The gate
    // itself is never held across an await: Stop closes it and cancels in one brief critical
    // section, then settles the session outside the gate (R-CD).
    private readonly SemaphoreSlim _launchGate = new(1, 1);

    // Set while Stop/Suspend is settling the session so no new launch can enter the gate even
    // after the brief critical section releases it (R-CD). Cleared when a fresh session begins.
    private volatile bool _launchGateClosed;

    // Session state (guarded by _operationLock for writes; reads via properties).
    private CancellationTokenSource? _sessionCts;
    private Task<SessionEnd>? _sessionTask;
    private SessionSnapshot? _session;

    // Persistence state owned by the controller.
    private CommittedSettingsSnapshot _committed = DefaultCommitted();
    private string _committedTargetPath = string.Empty;

    // Absent when monitoring is stopped; an empty pin is a fail-closed state (nothing launches).
    private string _latestPin = string.Empty;

    private MonitorState _state = new(GuardianStatus.Stopped, false, null, false);

    // Recovery pending flag: set when initialization reports ResetNoticePending and cleared only
    // by the controller-owned automatic recovery whose steps were all verified (task verified
    // absent -> AutostartEnabled=false + IsMonitoringEnabled=false persisted -> journal cleared).
    // While pending, Start is blocked at the controller level, ordinary autostart sync is blocked
    // (a pre-existing scheduler task is never enabled/created by plain settings commits or a
    // start), the checkbox is locked, and IsMonitoringEnabled stays off (v2.5 R-AS-AUTO).
    private bool _recoveryPending;

    // Automatic recovery: bounded delete/verify attempts per startup — never an unbounded timer
    // or background loop. A failed attempt leaves the journal pending; the next startup re-runs
    // the same idempotent flow and can safely finish after any crash boundary.
    private const int MaxRecoveryAttempts = 3;

    // Set when configuration initialization was observed as Refused (or threw
    // ConfigurationUnavailableException). While set, every mutation entry point is blocked at
    // the controller level: no settings save, no autostart mutation, no session. The guard is
    // intentionally independent of the UI disable state so a direct controller call cannot
    // bypass the refusal (R-A). ReconcileAsync is the only writer; a later successful
    // initialization re-opens the controller for the recovery/steady-state paths.
    private volatile bool _configRefused;

    // Latest verified actual autostart registration state + startup-sync error, published to the
    // presentation layer so a failed boot-time autostart sync is never lost to logs (2.4/2.5).
    public AutostartState? AutostartActualState { get; private set; }

    public string? AutostartSyncError { get; private set; }

    /// <summary>Raised whenever <see cref="AutostartActualState"/> / <see cref="AutostartSyncError"/> change.</summary>
    public event EventHandler? AutostartStateChanged;

    // --- Test seams ------------------------------------------------------------
    // Pause hooks placed at the two launch-gate boundaries so the launch-gate race tests are
    // deterministic: before entering the launch critical section, and inside it. Production
    // leaves them null. The inside hook is synchronous (Action) on purpose: while it runs the
    // launch gate IS held, so it must never await (R-CD).
    internal Func<SessionSnapshot, CancellationToken, Task>? BeforeLaunchGateHook { get; set; }
    internal Action? InsideLaunchGateHook { get; set; }

    // Test seam: invoked once each time a stability window completes (PhaseEnd.Stable) so
    // budget-reset tests synchronize deterministically. Production leaves it null.
    internal Action? StabilityConfirmedHook { get; set; }

    public GuardianOrchestrator(
        IProcessMonitor monitor,
        IConfigurationStore config,
        INotificationService notifications,
        ILogger<GuardianOrchestrator> logger,
        IStatusReporter status,
        ITargetIntegrityService integrity,
        IAutostartService autostart,
        TimeProvider? timeProvider = null,
        IResetNoticeAck? resetNoticeAck = null)
    {
        _monitor = monitor;
        _config = config;
        _notifications = notifications;
        _logger = logger;
        _status = status;
        _integrity = integrity;
        _autostart = autostart;
        _time = timeProvider ?? TimeProvider.System;
        _resetNoticeAck = resetNoticeAck;
    }

    public MonitorState CurrentState => _state;

    public event EventHandler<MonitorState>? StateChanged;

    /// <summary>Raised when the recovery-pending flag transitions (on successful recovery completion).</summary>
    public event EventHandler? RecoveryPendingChanged;

    /// <summary>True while a reset notice is pending and ordinary autostart sync is blocked.</summary>
    public bool RecoveryPending => _recoveryPending;

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
            // The store must resolve its initialization first (elevated edition: quarantine,
            // owner/DACL check, reset-notice latch). In the elevated edition a pending reset
            // notice means the previous config was quarantined and fresh defaults written: the
            // recovery policy demands an explicit user choice (A keep / B remove) before the
            // existing autostart task may be touched — no auto-sync, no auto-start, and the
            // alert is surfaced to the user.
            ConfigurationInitializationStatus init;
            try
            {
                init = await _config.InitializeAsync(ct).ConfigureAwait(false);
            }
            catch (ConfigurationUnavailableException ex)
            {
                _logger.LogError(ex, "Configuration unavailable at startup; monitoring will not run");
                // A refused/unavailable configuration is Error with a reason, never degraded:
                // IsDegraded is reserved for active session states (R-F).
                _configRefused = true;
                Publish(new MonitorState(GuardianStatus.Error, false, ex.Guidance ?? "Configuration is unavailable", false));
                return;
            }

            if (init == ConfigurationInitializationStatus.Refused)
            {
                _logger.LogError("Configuration initialization refused; monitoring will not run");
                // IsDegraded=false: a refused config is never a degraded session (R-F).
                _configRefused = true;
                Publish(new MonitorState(GuardianStatus.Error, false,
                    "Configuration could not be initialized; monitoring is off", false));
                return;
            }

            if (init == ConfigurationInitializationStatus.ResetNoticePending)
            {
                // [recovery policy, product v2.5 R-AS-AUTO] A pending reset notice means the
                // previous configuration was untrusted and quarantined and fresh safe defaults
                // were written. The controller now runs the automatic recovery BEFORE any
                // ordinary autostart sync and before any possibility of Start: remove the
                // (possibly pre-existing) elevated autostart task, persist AutostartEnabled=false
                // + IsMonitoringEnabled=false, and only then clear the journal. Until it settles,
                // _recoveryPending blocks Start at the controller level, no SyncAutostartAsync and
                // no session may run, and a failed recovery leaves the journal pending for the
                // next startup (idempotent). No A/B choice exists anymore — target path and pin
                // stay at their safe defaults and the user re-selects the target later.
                _configRefused = false;
                _recoveryPending = true;
                _logger.LogWarning("Reset notice pending; running automatic recovery (remove autostart, save disabled, clear journal)");
                var recovery = await RunAutomaticRecoveryAsync(ct).ConfigureAwait(false);
                if (recovery.Completed)
                {
                    // [v2.6 Amendment A] Fully verified recovery (task absent + disabled state
                    // persisted + journal cleared): publish the clean stopped state; the UI may
                    // unlock the ordinary controls (the pending gate re-opened above).
                    Publish(new MonitorState(GuardianStatus.Stopped, false, null, false));
                }
                else
                {
                    // [v2.6 Amendment A] All bounded attempts are exhausted and the journal is
                    // still pending: a locked recovery-failure Error, never a Stopped look-alike.
                    // IsDegraded=false — IsDegraded is reserved for active session states (R-F).
                    // Start must remain unavailable although the status is Error (Error alone
                    // would normally allow a new Start): the independent _recoveryPending gate
                    // stays set and both the ViewModel and the controller block Start. The next
                    // application startup retries the same idempotent flow; SaveFailed=true only
                    // when the recovery settings persistence itself failed, never as a generic
                    // recovery-failure flag.
                    Publish(new MonitorState(GuardianStatus.Error, false, RecoveryFailedReason, recovery.SaveFailed));
                }
                return;
            }

            AppSettings doc;
            try
            {
                doc = await _config.LoadAsync(ct).ConfigureAwait(false);
            }
            catch (ConfigurationUnavailableException ex)
            {
                _logger.LogError(ex, "Configuration unavailable at startup; monitoring will not run");
                // A refused/unavailable configuration is Error with a reason, never degraded:
                // IsDegraded is reserved for active session states (R-F). The controller-level
                // refusal flag is set here too, exactly like the InitializeAsync branches, so every
                // mutation entry point (CommitSettings/Start/Stop/Suspend/recovery) is blocked for
                // a load-time refusal as well — not only for an initialization refusal (R-A2).
                _configRefused = true;
                Publish(new MonitorState(GuardianStatus.Error, false, ex.Guidance ?? "Configuration is unavailable", false));
                return;
            }

            _committed = CommittedSettingsSnapshot.From(doc);
            _committedTargetPath = doc.TargetProcessPath;
            _latestPin = doc.TargetHashSha256;
            // A successful init re-opens the controller for the steady-state paths (the flag
            // is only an outcome of the most recent initialization, not a sticky failure).
            _configRefused = false;

            // Keeps the autostart registration in sync with the persisted flag on every
            // startup (moved with the persistence path; the ViewModel never writes it).
            // The result is published to the presentation layer so a failed boot-time sync
            // (or a state we could not verify) is surfaced to the user, not only logged.
            var autostart = await SyncAutostartAsync(_committed.AutostartEnabled, ct).ConfigureAwait(false);
            PublishAutostartState(autostart?.ActualState,
                autostart is { Succeeded: false } ? autostart.Value.ErrorMessage : null);

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

    public async Task<CommitSettingsResult> CommitSettingsAsync(
        CommittedSettingsSnapshot snapshot, string? targetPath, CancellationToken ct = default)
    {
        await _operationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_configRefused)
            {
                // [R-A] A refused configuration store admits no mutation at the controller level:
                // no settings save, no autostart change, no path/pin state change. A direct caller
                // (not only the disabled UI) is rejected here.
                _logger.LogWarning("Settings commit rejected: configuration initialization was refused");
                return CommitSettingsResult.SaveFailed();
            }

            var sessionActive = SessionActive;

            if (_recoveryPending)
            {
                // [v2.6 Amendment B / R-AS-GATE] While automatic recovery is pending, EVERY
                // user-originated settings commit is rejected: no SaveAsync, no autostart
                // SetEnabledAsync/RemoveRegistrationAsync, and no change to _committed,
                // _committedTargetPath or _latestPin (the controller-owned recovery save of the
                // safe disabled state runs through its own dedicated path,
                // PersistRecoveryStateAsync, and never routes through this method). The ordinary
                // settings/card controls are disabled in the UI while pending; this check is the
                // authoritative controller-level gate.
                _logger.LogWarning("Settings commit rejected: an automatic recovery is pending");
                return CommitSettingsResult.SaveFailed();
            }

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

            // 1) Initial settings save. A failed initial save must NOT be followed by an autostart
            //    side effect based on an uncommitted desired value (R-AS-TXN): the external
            //    scheduled task is only touched after a confirmed settings write.
            var saved = await PersistEnabledAsync(enabled: sessionActive, ct).ConfigureAwait(false);
            if (!sessionActive && saved)
                Publish(new MonitorState(GuardianStatus.Stopped, false, null, false));
            if (!saved)
                return CommitSettingsResult.SaveFailed();

            // 2) Autostart sync only after a successful initial save. The autostart result is
            //    surfaced with the commit so a registration failure is never masked by a successful
            //    settings save (the UI shows the error explicitly).
            var autostart = await SyncAutostartAsync(snapshot.AutostartEnabled, ct).ConfigureAwait(false);

            // 3) [R-AS-TXN rollback] A requested enable that the scheduler rejected must not leave
            //    a stale AutostartEnabled=true in the persisted config or the committed snapshot: a
            //    later launch would repeat the failed registration. Revert both to the value the UI
            //    also reverts its checkbox to (false) and re-persist as one consistent rollback —
            //    this is a re-persist of the corrected flag, never a fresh enable attempt. The
            //    compensating save result is checked: if the rollback save itself fails, the commit
            //    must NOT claim a fully persisted outcome (entity exception 3), the in-memory intent
            //    stays at the safe desired value false, and the failure is surfaced (not the disk's
            //    true, which we cannot confirm was rewritten). The autostart failure itself is still
            //    surfaced, not masked.
            if (autostart is { Succeeded: false } && snapshot.AutostartEnabled)
            {
                _committed = snapshot with { AutostartEnabled = false };
                var rollbackSaved = await PersistEnabledAsync(enabled: sessionActive, ct).ConfigureAwait(false);
                if (!rollbackSaved)
                {
                    _logger.LogError("Settings commit: autostart enable failed and the rollback persistence of AutostartEnabled=false also failed; in-memory intent retained as false, disk not confirmed false");
                    // Not persisted, but the autostart failure is still carried so the caller can
                    // distinguish "rollback save failed" (Saved=false + an autostart failure) from
                    // a plain initial-save failure (Saved=false, no autostart outcome).
                    return new CommitSettingsResult(false, autostart);
                }
                _logger.LogWarning("Settings commit: autostart enable failed; persisted intent reverted to AutostartEnabled=false");
                return CommitSettingsResult.Persisted(autostart);
            }

            return CommitSettingsResult.Persisted(autostart);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    // Controller-owned automatic recovery (v2.5 R-AS-AUTO): removes the app-owned autostart
    // task, persists AutostartEnabled=false + IsMonitoringEnabled=false, and only then clears
    // the journal. Runs inside ReconcileAsync before any ordinary autostart sync and before any
    // possibility of Start (_recoveryPending is set while it runs and stays set on failure).
    // Bounded to MaxRecoveryAttempts per startup; each step is verified and each failure leaves
    // the journal pending so the next startup re-runs the same idempotent flow (a crash after
    // the delete, after the save, or before the journal clear never recreates the task).
    // [v2.6 Amendment A] Returns an explicit outcome so ReconcileAsync can publish Stopped on
    // success and a locked recovery-failure Error on exhaustion without inferring it from the
    // last published status.
    /// <summary>
    /// Explicit outcome of the controller-owned automatic recovery (v2.6 Amendment A).
    /// <see cref="SaveFailed"/> is true only when the recovery settings persistence itself
    /// failed at least once during this startup's attempts — never a generic recovery-failure
    /// flag.
    /// </summary>
    internal readonly record struct AutomaticRecoveryResult(bool Completed, bool SaveFailed)
    {
        public static AutomaticRecoveryResult CompletedSuccessfully() => new(true, false);
        public static AutomaticRecoveryResult Exhausted(bool saveFailed) => new(false, saveFailed);
    }

    // Stable, bounded status reason published after recovery exhaustion (v2.6 Amendment A).
    // Raw command output and diagnostics stay in the log, never in the status text.
    internal const string RecoveryFailedReason = "Automatic recovery is incomplete; see the log for details.";

    // [v2.6 Amendment C] One-second cancellable delay between failed recovery attempts: exactly
    // one delay between each pair of failed attempts — never before the first attempt, never
    // after success, and never after the final failed attempt. Resolved through the injected
    // TimeProvider so tests stay deterministic (no wall-clock sleeps). Cancellation propagates
    // and is NOT an exhausted-recovery failure: the journal stays pending and the next startup
    // retries the same flow.
    private static readonly TimeSpan RecoveryRetryDelay = TimeSpan.FromSeconds(1);

    private async Task<AutomaticRecoveryResult> RunAutomaticRecoveryAsync(CancellationToken ct)
    {
        var saveFailed = false;
        for (var attempt = 1; attempt <= MaxRecoveryAttempts; attempt++)
        {
            if (attempt > 1)
            {
                _logger.LogWarning("Automatic recovery attempt {Attempt}/{Max} after a previous failure", attempt, MaxRecoveryAttempts);
                // Cancellable, TimeProvider-resolved delay (v2.6 Amendment C). Reaching attempt>1
                // implies the previous attempt failed, so this is exactly the between-attempts
                // delay; a successful recovery returned below and the final failed attempt ends
                // the loop, so neither gets a delay after it.
                await Task.Delay(RecoveryRetryDelay, _time, ct).ConfigureAwait(false);
            }

            // 1) Unconditional, positively-verified removal of the app-owned task. The service
            //    scopes the delete to the exact app task name; a not-found delete still confirms
            //    absence via a follow-up read. The /Delete exit code alone is never proof.
            AutostartResult removal;
            try
            {
                removal = await _autostart.RemoveRegistrationAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Automatic recovery attempt {Attempt}: autostart removal failed unexpectedly", attempt);
                continue;
            }

            if (!removal.Succeeded)
            {
                _logger.LogError("Automatic recovery attempt {Attempt}: autostart removal not verified: {Error}",
                    attempt, removal.ErrorMessage ?? "unknown");
                PublishAutostartState(removal.ActualState, removal.ErrorMessage);
                continue;
            }

            PublishAutostartState(AutostartState.Absent, null);

            // 2) Persist AutostartEnabled=false + IsMonitoringEnabled=false as one controller
            //    snapshot. Only a confirmed save proceeds to the journal clear; a failed save
            //    keeps the journal pending (task is already absent; the next attempt re-verifies).
            bool saved;
            try
            {
                saved = await PersistRecoveryStateAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Automatic recovery attempt {Attempt}: persisting the disabled state failed", attempt);
                saveFailed = true;
                continue;
            }

            if (!saved)
            {
                _logger.LogError("Automatic recovery attempt {Attempt}: settings save failed; journal stays pending", attempt);
                saveFailed = true;
                continue;
            }

            // 3) Only after verified task absence AND a successful save is the journal cleared.
            //    A clear failure keeps the journal pending; the next startup re-runs the same
            //    idempotent flow and finishes (the task is already absent, so the removal step
            //    becomes a non-destructive verification).
            if (_resetNoticeAck is null)
            {
                _logger.LogError("Automatic recovery attempt {Attempt}: reset-notice acknowledgement is unavailable; journal stays pending", attempt);
                continue;
            }

            try
            {
                await _resetNoticeAck.ClearAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Automatic recovery attempt {Attempt}: clearing the reset notice failed; journal stays pending", attempt);
                continue;
            }

            // 4) Fully verified: task absent + disabled state persisted + journal cleared.
            _recoveryPending = false;
            _logger.LogInformation("Automatic recovery completed: task verified absent, monitoring disabled, journal cleared");
            RecoveryPendingChanged?.Invoke(this, EventArgs.Empty);
            return AutomaticRecoveryResult.CompletedSuccessfully();
        }

        _logger.LogError("Automatic recovery failed after {Max} attempts; journal stays pending and Start stays blocked until a later startup retries",
            MaxRecoveryAttempts);
        return AutomaticRecoveryResult.Exhausted(saveFailed);
    }

    // Persists the post-recovery snapshot with explicit AutostartEnabled=false and
    // IsMonitoringEnabled=false, regardless of _committed.AutostartEnabled (which may carry a UI
    // intent that must not leak into the recovery). Target path and pin stay at their current
    // (safe, default) values — the user re-selects the target later. This is the only autostart
    // flag write on the recovery path.
    private async Task<bool> PersistRecoveryStateAsync(CancellationToken ct)
    {
        try
        {
            var doc = await _config.LoadAsync(ct).ConfigureAwait(false);
            ApplyCommitted(doc, _committed);
            doc.TargetProcessPath = _committedTargetPath;
            doc.TargetHashSha256 = _latestPin;
            doc.AutostartEnabled = false;
            doc.IsMonitoringEnabled = false;
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
            _logger.LogError(ex, "Persist recovery state (AutostartEnabled=false, IsMonitoringEnabled=false) failed");
            return false;
        }
    }

    public async Task<StartResult> StartAsync(StartRequest request, CancellationToken ct = default)
    {
        await _operationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_configRefused)
            {
                // [R-A] No session, no hash read, no persistence and no autostart mutation while
                // the configuration store is refused: Start fails closed with guidance.
                _logger.LogWarning("Start rejected: configuration initialization was refused");
                return new StartResult(false, "monitoring cannot start: the configuration is unavailable");
            }

            if (_recoveryPending)
            {
                // [recovery policy v2.5 R-AS-AUTO] While automatic recovery is pending (task not
                // verified absent / journal not cleared), the controller itself blocks Start — not
                // only the UI. No monitoring may begin, and no autostart enable/create may run,
                // until the recovery settles. A later startup retries the same idempotent flow.
                _logger.LogWarning("Start rejected: an automatic recovery is pending (task not verified absent / journal not cleared)");
                return new StartResult(false, "monitoring cannot start until the pending recovery completes");
            }

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
            if (!await PersistEnabledAsync(enabled: true, ct, applyAutostart: !_recoveryPending).ConfigureAwait(false))
            {
                // Persistence failure is surfaced via SaveFailed, never by marking degraded:
                // a stopped session cannot be degraded (R-F).
                Publish(new MonitorState(GuardianStatus.Stopped, false, "settings could not be persisted; monitoring not started", true));
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
            if (_configRefused)
            {
                // [R-A] A refused store cannot be written; nothing is running and nothing may be
                // persisted or synced.
                // [v2.6 Amendment E / R-A-ERROR-STATE] The refused Error state stays intact: the
                // existing reason is preserved, IsDegraded stays false, and Stopped is never
                // published over it.
                _logger.LogWarning("Stop: configuration initialization was refused; no persistence attempted");
                Publish(new MonitorState(GuardianStatus.Error, false,
                    _state.Reason ?? "Configuration is unavailable", false));
                return StopResult.AlreadyStopped();
            }

            if (latestCommittedSettings is not null)
                _committed = latestCommittedSettings;

            var cts = _sessionCts;
            var task = _sessionTask;
            if (cts is null || task is { IsCompleted: true })
            {
                // No live session: tear down the (possibly completed) session holder here too so
                // an in-flight fatal settlement continuation observes a stale reference and skips
                // its persist/publish (it re-checks inside the operation lock). Persist the desired
                // off-state best-effort and report stopped — never degraded (R-F).
                _session = null;
                cts?.Dispose();
                _sessionCts = null;
                _sessionTask = null;

                var saved = await PersistEnabledAsync(enabled: false, ct, applyAutostart: !_recoveryPending).ConfigureAwait(false);
                Publish(new MonitorState(GuardianStatus.Stopped, false, null, !saved));
                return saved ? StopResult.AlreadyStopped() : StopResult.RuntimeStopped_SaveFailed(new Exception("persisting the disabled state failed"));
            }

            // Close the launch gate and cancel the session in one brief critical section (R-CD):
            // no Process.Start may begin after the gate closes, and none may occur after this
            // Stop returns. An in-flight launch critical section holds the gate; this wait makes
            // RuntimeStopped honest. The gate is released before the session is awaited so Stop
            // never blocks on the full verification timeout — a verification already running
            // observes the cancellation as soon as the key is turned. The _launchGateClosed flag
            // keeps the gate logically closed to new starts while the session settles.
            await _launchGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                _launchGateClosed = true;
                _sessionCts?.Cancel();
            }
            finally
            {
                _launchGate.Release();
            }

            try
            {
                await (task!).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Session task faulted during Stop");
            }

            _session = null;
            _sessionCts?.Dispose();
            _sessionCts = null;
            _sessionTask = null;

            // One final store save merging committed fields with controller-owned state.
            var settingsSaved = await PersistEnabledAsync(enabled: false, ct, applyAutostart: !_recoveryPending).ConfigureAwait(false);
            Publish(new MonitorState(GuardianStatus.Stopped, false, null, !settingsSaved));
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
            if (_configRefused)
            {
                // [R-A] Nothing to cancel and nothing to persist in a refused state.
                // [v2.6 Amendment E / R-A-ERROR-STATE] The refused Error state stays intact: the
                // existing reason is preserved, IsDegraded stays false, and Stopped is never
                // published over it.
                Publish(new MonitorState(GuardianStatus.Error, false,
                    _state.Reason ?? "Configuration is unavailable", false));
                return;
            }

            var cts = _sessionCts;
            var task = _sessionTask;
            if (cts is null || task is { IsCompleted: true })
            {
                await _launchGate.WaitAsync(ct).ConfigureAwait(false);
                try { /* nothing to cancel */ }
                finally { _launchGate.Release(); }
                return;
            }

            // Close the launch gate and cancel in one brief critical section, then settle the
            // session outside the gate (R-CD): the session-token cancellation unblocks any
            // verification that was already running, and _launchGateClosed keeps the gate
            // logically closed to new launches while the session settles.
            await _launchGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                _launchGateClosed = true;
                _sessionCts?.Cancel();
            }
            finally
            {
                _launchGate.Release();
            }

            try
            {
                await (task!).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Session task faulted during Suspend");
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
        // A fresh session re-opens the launch gate: any previous Stop/Suspend close is void.
        _launchGateClosed = false;
        _sessionCts = new CancellationTokenSource();
        _session = session;
        var cts = _sessionCts;
        _sessionTask = Task.Run(() => RunSessionAsync(session, cts.Token));
        // Fatal causes (budget exhaustion, hash mismatch) leave the session loop as a terminal
        // outcome. The session task finishes first; a continuation outside it then persists
        // enabled=false and publishes Error even if that save fails (SaveFailed independent).
        // The guard skips the publish if a newer session already replaced this one (R-F).
        _ = SettleSessionOutcomeAsync(_sessionTask);
    }

    private async Task SettleSessionOutcomeAsync(Task<SessionEnd> sessionTask)
    {
        SessionEnd outcome;
        try
        {
            outcome = await sessionTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Session task faulted; no fatal outcome was settled");
            return;
        }

        string? fatalReason = outcome switch
        {
            SessionEnd.HashMismatch => "the target binary no longer matches the recorded pin; monitoring stopped",
            SessionEnd.BudgetExhausted => "the per-session automatic restart budget is exhausted; monitoring stopped",
            _ => null,
        };
        if (fatalReason is null)
            return;

        // [R-F settlement sync] Settle under the operation lock, with the liveness re-check inside
        // the lock: Stop/Suspend tear the session holder down and a new Start replaces it, so a
        // stale fatal continuation — one that was already in flight past its first await while the
        // persist ran — observes a stale reference and skips both the publish and the persist.
        // Serializing the publish-and-persist with Start/Stop means a Stop during the settlement
        // either wins the lock first (holder nulled -> this continuation skips) or runs after it
        // and publishes Stopped last; either way a stale Error is never the final published state,
        // and a late IsMonitoringEnabled=false / AutostartEnabled=false cannot disable a newer
        // session whose Start is linearized after (or re-persists over) this settlement.
        await _operationLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(_sessionTask, sessionTask))
                return;

            // Publish the terminal Error IMMEDIATELY (§4.2/§4.3): the final reason is complete on
            // the first display and the card is never shown with an intermediate text first. The
            // enabled=false persistence below is best-effort and must never delay the first Error
            // rendering; a persistence failure only updates the independent SaveFailed flag and
            // always preserves this original reason.
            Publish(new MonitorState(GuardianStatus.Error, false, fatalReason, false));

            // Persist enabled=false best-effort — a failed save never turns the terminal state
            // into degraded; the failure is surfaced via the independent SaveFailed flag while
            // the Error reason stays intact (§4.3 step 5).
            await TryPersistDisabledBestEffortAsync(_committed).ConfigureAwait(false);
            if (_lastPersistFailed)
                Publish(new MonitorState(GuardianStatus.Error, false, fatalReason, true));
        }
        finally
        {
            _operationLock.Release();
        }
    }

    // v2.8.1 (R-AS-RESTART-CADENCE): the session is a single non-blocking event loop, owned by
    // the session task, that services independently of each other:
    //   - the next normal supervision tick (CheckIntervalSeconds): recovery decisions and
    //     automatic launches happen on these ticks — never on a phase deadline, never on a
    //     bare deadline wake — and the tick schedule is deadline-anchored (one interval per
    //     serviced tick, drift-free, no extra interval added after the work completes);
    //   - startup-appearance/stability observation: while a phase is active the loop observes
    //     at the bounded PhasePollMs cadence; a poll landing at/after the next tick's deadline
    //     IS that normal tick, so observation never postpones a normal tick and no separate
    //     observer task exists that could block, postpone or race the supervision loop;
    //   - monotonic deadlines: the RestartDelaySeconds spacing clock is a single source of
    //     truth — the completion timestamp of the last actual creation attempt (Started or
    //     a genuine Failed — the free initial attempt included) — and the deadline is
    //     derived from it each cycle against the CURRENT committed RestartDelaySeconds
    //     (§3.3); it is never touched by an observation event: a fall, an appearance
    //     timeout, a loss detection or an Unknown never change the reference (§3.2).
    //     The appearance deadline and the stability boundary are computed from TimeProvider
    //     timestamps and are only ever re-evaluated on observations — never awaited inline,
    //     never busy-looped;
    //   - cancellation: the only waits are token-linked Task.Delay waits plus the launch-gate
    //     critical section, so Stop/Suspend settle promptly and no late launch can occur.
    // The budget (RestartAttempts) is charged exactly once at the actual automatic
    // process-creation boundary and replenished only by a full continuous stability window
    // (v2.8); the stability qualification never blocks a check, a retry decision, an eligible
    // launch or the recovery notification.
    private async Task<SessionEnd> RunSessionAsync(SessionSnapshot session, CancellationToken ct)
    {
        var counters = new SessionCounters();
        // Path form is fixed for the whole session (validated at Start/Reconcile); the monitor
        // checks existence/readability at runtime per E — the loop never re-runs form validation.
        var target = new ProcessTarget(session.TargetProcessPath, session.TargetNameWithoutExtension, session.WorkingDirectory);
        try
        {
            var knobs = _committed; // numeric knobs re-read each cycle; path+pin stay fixed

            // Deadline-anchored normal cadence (v2.8.1 §B.1): the next tick is an absolute
            // monotonic deadline, advanced by exactly one interval per serviced tick.
            var initialDelay = TimeSpan.FromSeconds(knobs.InitialDelaySeconds);
            var nextCheckDue = _time.GetTimestamp() + DeadlineTicks(initialDelay);
            if (initialDelay > TimeSpan.Zero)
                Publish(new MonitorState(GuardianStatus.Waiting, false, null, false));

            // Attempt/qualification state, all session-owned by this loop (no separate observer
            // task exists; nothing here ever launches from a detached callback).
            long? attemptLaunchedAt = null;    // actual creation-attempt timestamp of the outstanding
                                               // launch awaiting its first positive appearance
            long? runningSince = null;         // stability-qualification start; null = not qualifying
            bool restartPending = false;       // the target is down and a launch decision is pending
            long? lastCreationAttemptAt = null; // completion timestamp of the last actual creation
                                                // attempt (Started or a genuine Failed, the free
                                                // initial attempt included); null = no spacing
                                                // reference = a launch is allowed. The
                                                // RestartDelaySeconds deadline is derived per cycle
                                                // from this value and the CURRENT committed
                                                // RestartDelaySeconds (R-AS-RECOVERY-CORRECTNESS
                                                // §3.3), so a mid-session setting change extends or
                                                // shortens the remaining interval; it is the single
                                                // source of truth for the spacing clock.
            bool toastPending = false;         // the current automatic attempt has not shown its
                                               // recovery toast (sent only after the attempt's FULL
                                               // stability window completes — §6.2)

            while (!ct.IsCancellationRequested)
            {
                // ---- wait for the earliest event (normal tick, phase poll) ----
                var phaseActive = attemptLaunchedAt is not null || runningSince is not null;
                var now = _time.GetTimestamp();
                var wake = nextCheckDue;
                if (phaseActive)
                {
                    // The bounded non-blocking observation cadence for the appearance/stability
                    // phases (v2.8 §4.2): independent of CheckIntervalSeconds (which may far
                    // exceed the one-second windows) and never a substitute for the tick
                    // schedule below. Detection precision is finite — ~PhasePollMs plus the
                    // check duration — and is documented rather than claimed exact.
                    var nextPoll = now + DeadlineTicks(TimeSpan.FromMilliseconds(PhasePollMs));
                    if (nextPoll < wake)
                        wake = nextPoll;
                }

                if (!await WaitUntilAsync(wake, ct).ConfigureAwait(false))
                    return SessionEnd.Cancelled;

                now = _time.GetTimestamp();
                // Re-read the committed knobs AT THE DECISION POINT (§3.3): a settings commit
                // that landed while the loop was asleep must take effect on the cycle it wakes
                // into — the deadline is derived from the CURRENT committed RestartDelaySeconds,
                // and the budget/timeout/toast knobs are read fresh per cycle as well.
                knobs = _committed;
                var stabilityDuration = TimeSpan.FromSeconds(knobs.StartupVerificationTimeoutSeconds);
                var restartDelay = TimeSpan.FromSeconds(knobs.RestartDelaySeconds);

                // ---- observation (one CheckAsync services every due purpose) ----
                // A poll landing at/after the next tick's deadline services that normal tick:
                // the next tick is one full interval later (anchored, drift-free). If a single
                // check ever overruns its interval, the schedule re-anchors instead of bursting
                // catch-up checks.
                var isNormalTick = now >= nextCheckDue;
                if (isNormalTick)
                {
                    nextCheckDue += DeadlineTicks(TimeSpan.FromSeconds(knobs.CheckIntervalSeconds));
                    if (nextCheckDue <= now)
                        nextCheckDue = now + DeadlineTicks(TimeSpan.FromSeconds(knobs.CheckIntervalSeconds));
                }

                ProcessOutcome outcome;
                try
                {
                    outcome = (await _monitor.CheckAsync(target, ct).ConfigureAwait(false)).Outcome;
                }
                catch (OperationCanceledException)
                {
                    return SessionEnd.Cancelled;
                }
                catch (Exception ex)
                {
                    // F (SPEC §7.2.1/EH-02): a throwing check behaves like Unknown — the session
                    // stays alive, degraded, and never escalates to Error on its own.
                    _logger.LogWarning(ex, "CheckAsync failed for {Target}; treating the cycle as unknown", target.NameWithoutExtension);
                    outcome = ProcessOutcome.Unknown;
                }

                switch (outcome)
                {
                    case ProcessOutcome.Running:
                        attemptLaunchedAt = null; // positively observed: the appearance window is over
                        restartPending = false;   // the target is up; no launch is needed
                        if (runningSince is null)
                        {
                            runningSince = now;
                            ResetDegraded(counters);
                            Publish(new MonitorState(GuardianStatus.Monitoring, false, null, false));
                        }
                        else if (_time.GetElapsedTime(runningSince.GetValueOrDefault()) >= stabilityDuration)
                        {
                            // Fresh positive observation at/after the full duration: stable
                            // (v2.8 §3.3). This is the only budget refund during a session.
                            StabilityConfirmedHook?.Invoke();
                            counters.RestartAttempts = 0;
                            // The entry qualification resolved stably (v2.8 §3.1.2): the free
                            // launch is spent — the first actual restart of this session is
                            // budgeted, preserving the old PhaseEnd.Stable semantics.
                            counters.InitialLaunchPending = false;
                            runningSince = null;
                            ResetDegraded(counters);
                            Publish(new MonitorState(GuardianStatus.Monitoring, false, null, false));
                            if (toastPending)
                            {
                                // R-AS-RECOVERY-CORRECTNESS §6.2: the recovery toast is sent only
                                // after the attempt's FULL continuous stability window completes —
                                // the first positive Running observation alone proves appearance,
                                // not recovery, so a process that dies inside the window never
                                // produces a false "Process Recovered". At most once per attempt
                                // (cleared here), never for a failed attempt (never pending), and
                                // never blocking supervision (the delivery task is observed).
                                toastPending = false;
                                StartRecoveryToastSafe(knobs, ct);
                            }
                        }
                        break;

                    case ProcessOutcome.NotRunning:
                    {
                        // Set the decision fields first; the terminal determination (below) and
                        // the Restarting publication are made only after the whole sub-case state
                        // is established, so a cycle that is already terminal never publishes the
                        // intermediate Restarting first (§4.2).
                        var restartDecided = false;
                        var publishRestarting = false;
                        if (runningSince is not null)
                        {
                            // Reliable absence during stability qualification: an unstable run,
                            // never a refund (v2.8 §3.3). The launch is decided on the normal
                            // ticks below (v2.8.1 §B.2), never on this phase poll directly, and
                            // the spacing clock is NOT touched here: it stays anchored to the
                            // previous actual creation attempt (§3.2).
                            runningSince = null;
                            toastPending = false;
                            restartPending = true;
                            restartDecided = true;
                            publishRestarting = true;
                            // A qualification that began with the target already running (the
                            // free launch unused) resolves here: per v2.8 §3.1.2/§3.4 the first
                            // actual restart of such a session is budgeted, so the free launch
                            // is consumed by the resolved entry observation.
                            counters.InitialLaunchPending = false;
                        }
                        else if (attemptLaunchedAt is not null)
                        {
                            // Appearance/probation window: a reliable absence before the deadline
                            // is not yet a failure (the launched process may still be starting
                            // up). At/after the deadline the attempt is marked not established
                            // (v2.8.1 §B.3); no charge is added — the attempt was already charged
                            // at its creation boundary. The next launch then happens on a normal
                            // tick, constrained only by the remaining budget and the spacing
                            // reference of that actual attempt — the timeout expiry never touches
                            // the spacing clock (§3.2 items 6–9).
                            if (now - attemptLaunchedAt.GetValueOrDefault() >= DeadlineTicks(stabilityDuration))
                            {
                                attemptLaunchedAt = null;
                                toastPending = false;
                                restartPending = true;
                                restartDecided = true;
                                publishRestarting = true;
                                _logger.LogInformation(
                                    "Launched process {Target} did not appear within the startup timeout", target.NameWithoutExtension);
                                counters.InitialLaunchPending = false;
                            }
                        }
                        else
                        {
                            // A loss detected on a check enters the restart decision path on that
                            // cycle (v2.8.1 §B.1). No inline delay is ever awaited and the
                            // spacing clock is NOT re-armed here: the spacing reference is the
                            // previous actual creation attempt (the free initial attempt
                            // included — freeness means no budget charge, not a missing
                            // attempt, §3.2), so the launch evaluation below is gated only by
                            // that reference, the budget and this fresh NotRunning observation.
                            restartPending = true;
                            restartDecided = true;
                            // During the session's free-launch retry the state stays Waiting;
                            // otherwise the pending restart is user-visible.
                            publishRestarting = !counters.InitialLaunchPending;
                        }

                        if (restartDecided)
                        {
                            // Terminal determination (§4.2): this cycle has just established a
                            // reliable absence together with a pending restart decision that the
                            // budget cannot fund — the outcome is already terminal. Publish no
                            // intermediate Restarting: the settle continuation publishes the
                            // final Error immediately (before any persistence I/O), and no
                            // stale Restarting can follow it.
                            if (knobs.MaxRestartAttempts > 0 && counters.RestartAttempts >= knobs.MaxRestartAttempts)
                            {
                                _logger.LogError("Automatic restart budget ({Max}) exhausted for {Target}; no stable run was established",
                                    knobs.MaxRestartAttempts, target.NameWithoutExtension);
                                try
                                {
                                    if (knobs.EnableToasts)
                                        await _notifications.ShowAsync("Recovery Failed", string.Empty, ct).ConfigureAwait(false);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogWarning(ex, "Failed to show exhaustion toast");
                                }

                                return SessionEnd.BudgetExhausted;
                            }

                            if (publishRestarting)
                                Publish(new MonitorState(GuardianStatus.Restarting, false, null, false));
                        }

                        break;
                    }

                    case ProcessOutcome.Unknown:
                        // Prolonged-Unknown discipline (E, v2.8 §3.2/§3.3): never absence, never
                        // proof of stability, never a duplicate launch, never a refund. An open
                        // stability interval is voided; a fresh full interval is required later.
                        // A pending recovery toast stays pending: the attempt has not completed
                        // its full stability window (§6.2), and the next fresh positive Running
                        // starts a new one.
                        _logger.LogWarning("Process state for {Target} could not be determined; skipping this cycle",
                            target.NameWithoutExtension);
                        MarkDegraded(counters);
                        runningSince = null;
                        break;
                }

                // ---- launch evaluation (normal supervision tick only, v2.8.1 §B.2) ----
                // A pending restart is attempted on a tick whose fresh observation found the
                // target reliably NotRunning and only after the RestartDelaySeconds spacing
                // interval — derived from the last COMPLETED actual creation attempt and the
                // CURRENT committed RestartDelaySeconds (§3.3: a mid-session setting change
                // extends or shortens the remaining interval, and a change can never allow an
                // earlier start than the newly computed deadline) — elapsed. The budget
                // exhaustion decision is made in the observation branch above (§4.2), so the
                // launch evaluation only runs when the budget still permits the attempt.
                var restartDelayElapsed = lastCreationAttemptAt is null
                    || now - lastCreationAttemptAt.GetValueOrDefault() >= DeadlineTicks(restartDelay);
                if (restartPending
                    && attemptLaunchedAt is null
                    && runningSince is null
                    && outcome == ProcessOutcome.NotRunning
                    && isNormalTick
                    && restartDelayElapsed)
                {
                    var automatic = !counters.InitialLaunchPending;
                    var (launch, attemptTimestamp) = await AttemptLaunchAsync(target, knobs, counters,
                        automatic, ct).ConfigureAwait(false);
                    switch (launch)
                    {
                        case LaunchOutcome.Cancelled:
                            return SessionEnd.Cancelled;

                        case LaunchOutcome.Mismatch:
                            // Fail closed (K): the fatal integrity path is independent of the budget.
                            return SessionEnd.HashMismatch;

                        case LaunchOutcome.BlockedDegraded:
                            // Blocked before the OS process-creation boundary (hash/launch Missing
                            // or ReadError, indeterminate adapter exception): uncharged, the
                            // restart stays pending and is reconsidered on the next tick (E).
                            MarkDegraded(counters);
                            break;

                        case LaunchOutcome.StartFailed:
                            // Genuine creation failure (v2.8 §3.2): charged exactly once at the
                            // boundary (inside AttemptLaunchAsync); the free initial attempt stays
                            // free. The spacing reference is this COMPLETED attempt (its
                            // post-TryStart timestamp, §3.1); the next attempt is re-decided on a
                            // normal tick.
                            counters.InitialLaunchPending = false;
                            restartPending = true;
                            lastCreationAttemptAt = attemptTimestamp;

                            // Terminal determination (§4.2): the creation failure of the last
                            // budgeted attempt has just exhausted the budget on this same cycle
                            // while the target is reliably absent — the outcome is terminal and
                            // the Error is published immediately (no further confirmation cycle
                            // and no restart delay can change it).
                            if (knobs.MaxRestartAttempts > 0 && counters.RestartAttempts >= knobs.MaxRestartAttempts)
                            {
                                _logger.LogError("Automatic restart budget ({Max}) exhausted for {Target}; no stable run was established",
                                    knobs.MaxRestartAttempts, target.NameWithoutExtension);
                                try
                                {
                                    if (knobs.EnableToasts)
                                        await _notifications.ShowAsync("Recovery Failed", string.Empty, ct).ConfigureAwait(false);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogWarning(ex, "Failed to show exhaustion toast");
                                }

                                return SessionEnd.BudgetExhausted;
                            }

                            break;

                        case LaunchOutcome.Launched:
                            // Started: charged once at the creation boundary (automatic). The
                            // appearance/probation window begins at this actual attempt; the FULL
                            // stability window sends the recovery toast (§6.2). The free attempt
                            // also forms the spacing reference (§3.2).
                            counters.InitialLaunchPending = false;
                            attemptLaunchedAt = attemptTimestamp;
                            restartPending = false;
                            lastCreationAttemptAt = attemptTimestamp;
                            toastPending = automatic;
                            break;
                    }
                }
            }
        }
        finally
        {
            // Only the session task clears the session; it is the source of truth for "session over".
            _session = null;
        }

        return SessionEnd.Cancelled;
    }

    /// <summary>Terminal/loop outcome of a monitoring session (R-F). Only
    /// <see cref="SessionEnd.HashMismatch"/> and <see cref="SessionEnd.BudgetExhausted"/> are
    /// fatal and are settled outside the session task by <see cref="SettleSessionOutcomeAsync"/>.</summary>
    private enum SessionEnd
    {
        /// <summary>Continue the check loop.</summary>
        Continue,
        /// <summary>The session ended without a fatal cause (routine terminal).</summary>
        Normal,
        /// <summary>The session was cancelled (Stop/Suspend or token cancellation).</summary>
        Cancelled,
        /// <summary>Fatal: the target binary no longer matches the recorded pin.</summary>
        HashMismatch,
        /// <summary>Fatal: the non-zero instability-streak budget was consumed with no stable
        /// run established (v2.8).</summary>
        BudgetExhausted,
    }

    private sealed class SessionCounters
    {
        /// <summary>Automatic launch attempts consumed since the last confirmed stable run (v2.8):
        /// the per-session instability-streak budget behind <c>MaxRestartAttempts</c>. Every
        /// genuine automatic process-creation attempt increments it exactly once at the creation
        /// boundary, regardless of the attempt's outcome; completing a full stability window
        /// resets it to zero (the only refund during a session). Only a newly created monitoring
        /// session starts it at zero.</summary>
        public int RestartAttempts;
        /// <summary>True while the session's free launch (v2.8 §3.1) is still unused: the first
        /// actual <c>TryStart</c> of the session is uncharged, and it stops being pending at the
        /// creation boundary (Started/Failed) or when an entry qualification resolves (a stable
        /// or fallen entry-Running observation). A free launch blocked before the OS creation
        /// boundary (hash/TryStart Missing or ReadError, adapter exception) keeps it pending,
        /// so the free attempt is retried without spending budget.</summary>
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

    /// <summary>Typed outcome of a launch attempt within one supervision tick (v2.8.1).
    /// <see cref="LaunchOutcome.Launched"/> means the process was created (charged at the
    /// boundary when automatic) and its appearance window begins. <see cref="LaunchOutcome.StartFailed"/>
    /// means a genuine creation failure (charged once at the boundary when automatic).
    /// <see cref="LaunchOutcome.BlockedDegraded"/> means the flow was blocked before the OS
    /// process-creation boundary or is indeterminate — always uncharged.
    /// <see cref="LaunchOutcome.Mismatch"/> is the separate fatal integrity path;
    /// <see cref="LaunchOutcome.Cancelled"/> is routine session cancellation (never Error, never charged).
    /// The result carries the completion timestamp captured immediately after the synchronous
    /// <c>TryStart</c> returned (§3.1); the session loop commits it as the restart-spacing
    /// reference only for a <c>Started</c> result or a genuine <c>Failed</c> — every other
    /// outcome leaves the previous reference untouched.</summary>
    private enum LaunchOutcome { Launched, StartFailed, BlockedDegraded, Mismatch, Cancelled }

    // The launch boundary of one supervision tick (v2.8.1 §B.2): hash gate → launch gate →
    // synchronous TryStart, with the budget charged exactly once at the actual automatic
    // process-creation boundary (Started or a genuine Failed). No timed phase is awaited
    // here: appearance/stability observation is serviced by the session event loop itself,
    // so a launch can never hold a check, a retry decision or a notification. Returns the
    // outcome plus the completion timestamp captured immediately after the synchronous
    // TryStart returned (§3.1) — the spacing reference for the next attempt.
    private async Task<(LaunchOutcome Outcome, long AttemptTimestamp)> AttemptLaunchAsync(
        ProcessTarget target, CommittedSettingsSnapshot knobs, SessionCounters counters,
        bool automatic, CancellationToken ct)
    {
        var hash = await VerifyTargetHashAsync(target, knobs, ct).ConfigureAwait(false);
        switch (hash)
        {
            case HashVerifyResult.Cancelled:
                return (LaunchOutcome.Cancelled, 0);
            case HashVerifyResult.Missing:
                _logger.LogWarning("Target {Target} is missing before launch; skipping (degraded), no budget spent",
                    target.NameWithoutExtension);
                return (LaunchOutcome.BlockedDegraded, 0);
            case HashVerifyResult.ReadError:
                _logger.LogWarning("Target {Target} could not be read before launch; skipping (degraded), no budget spent",
                    target.NameWithoutExtension);
                return (LaunchOutcome.BlockedDegraded, 0);
            case HashVerifyResult.Mismatch:
                // Fail closed (K): hash mismatch is fatal. The enabled=false persistence and the
                // Error publish are deferred to the outside-the-task continuation
                // (SettleSessionOutcomeAsync, R-F), so this method only returns the terminal
                // outcome and never publishes Error from inside the session task.
                _logger.LogError("Target {Target} no longer matches its recorded pin; terminating the session",
                    target.NameWithoutExtension);
                return (LaunchOutcome.Mismatch, 0);
        }

        // Linearized launch (R-CD): only the final session/gate check and the synchronous
        // Process.Start run while _launchGate is held. The gate is released immediately after
        // the process-creation result is captured — observation, toasts and delays all run
        // outside it and remain cancellable by the session token. Stop/Suspend close the gate
        // and cancel the session in one brief critical section, then settle it outside the
        // gate, so no launch begins after they act and none begins after a completed Stop
        // returns.
        if (BeforeLaunchGateHook is not null)
            await BeforeLaunchGateHook(CurrentSessionOr(target), ct).ConfigureAwait(false);

        await _launchGate.WaitAsync(ct).ConfigureAwait(false);
        ProcessStartResult start;
        long attemptTimestamp;
        try
        {
            // Re-check session identity under the gate; no await may precede the sync launch.
            if (ct.IsCancellationRequested || _launchGateClosed)
                return (LaunchOutcome.Cancelled, 0);

            InsideLaunchGateHook?.Invoke();

            // The spacing reference is the COMPLETION timestamp of the actual creation attempt
            // (§3.1): it is captured immediately AFTER the synchronous TryStart returns, so a
            // TryStart that takes non-zero time extends the measured interval instead of eating
            // into it — the next attempt is never earlier than a full RestartDelaySeconds after
            // the previous attempt returned. The session loop commits it as the spacing
            // reference only after the result confirms Started or a genuine Failed; every other
            // outcome leaves the previous reference untouched.
            start = _monitor.TryStart(target, ct);
            attemptTimestamp = _time.GetTimestamp();
        }
        catch (OperationCanceledException)
        {
            return (LaunchOutcome.Cancelled, 0);
        }
        catch (Exception ex)
        {
            // Indeterminate adapter exception (v2.7 §2.3, preserved): the concrete adapter
            // converts every exception inside its launch boundary into a typed result, so an
            // exception here is not an observed genuine creation failure — it stays uncharged
            // (fail-safe: never spends the budget on an unobserved attempt) and keeps the
            // session alive.
            _logger.LogError(ex, "TryStart threw for {Target}", target.NameWithoutExtension);
            return (LaunchOutcome.BlockedDegraded, 0);
        }
        finally
        {
            // Released immediately after the start result is captured (R-CD); never held across
            // an await, so the observation below cannot block Stop.
            _launchGate.Release();
        }

        switch (start.Status)
        {
            case ProcessStartStatus.Cancelled:
                return (LaunchOutcome.Cancelled, 0);
            case ProcessStartStatus.Missing:
                _logger.LogWarning("Target {Target} disappeared before launch; skipping (degraded), no budget spent",
                    target.NameWithoutExtension);
                return (LaunchOutcome.BlockedDegraded, 0);
            case ProcessStartStatus.ReadError:
                _logger.LogWarning("Target {Target} became unreadable before launch; skipping (degraded), no budget spent",
                    target.NameWithoutExtension);
                return (LaunchOutcome.BlockedDegraded, 0);
            case ProcessStartStatus.Failed:
                // Immediate genuine creation failure (v2.8 §3.2): charged exactly once here for
                // an automatic attempt; the free initial attempt stays free. No appearance timer
                // is needed — the creation operation has already returned failure.
                _logger.LogError("Failed to start process {Target}: {Error}", target.NameWithoutExtension, start.ErrorMessage);
                if (automatic)
                {
                    counters.RestartAttempts++;
                    _logger.LogInformation("Automatic launch attempt {Attempts} charged for {Target}",
                        counters.RestartAttempts, target.NameWithoutExtension);
                }
                return (LaunchOutcome.StartFailed, attemptTimestamp);
        }

        // Started (v2.8 §2.1): the single charging point for an automatic attempt — charged
        // once here at the actual process-creation boundary. The appearance deadline, exit and
        // stability events never charge again, so one attempt cannot be counted twice.
        if (automatic)
        {
            counters.RestartAttempts++;
            _logger.LogInformation("Automatic launch attempt {Attempts} charged for {Target}",
                counters.RestartAttempts, target.NameWithoutExtension);
        }

        return (LaunchOutcome.Launched, attemptTimestamp);
    }

    // v2.8.1 §D: the recovery toast fires on the first positive Running observation after an
    // automatic restart — never merely because Process.Start returned Started, at most once
    // per attempt, and never duplicated for an attempt that exits before stability. Delivery
    // never blocks the supervision loop, and the detached task is fully observed: every
    // exception path is caught and logged, so nothing escapes as an unobserved task fault.
    private void StartRecoveryToastSafe(CommittedSettingsSnapshot knobs, CancellationToken ct)
    {
        if (!knobs.EnableToasts)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await _notifications.ShowAsync("Process Recovered", string.Empty, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Routine cancellation (Stop/Suspend): never an error.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to show recovery toast");
            }
        });
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

    private async Task<bool> PersistEnabledAsync(bool enabled, CancellationToken ct, bool applyAutostart = true)
    {
        try
        {
            var doc = await _config.LoadAsync(ct).ConfigureAwait(false);
            var preservedAutostart = doc.AutostartEnabled;
            ApplyCommitted(doc, _committed);
            doc.TargetProcessPath = _committedTargetPath;
            doc.TargetHashSha256 = _latestPin;
            doc.IsMonitoringEnabled = enabled;
            if (!applyAutostart)
                doc.AutostartEnabled = preservedAutostart; // never overwrite the flag on a plain save; the recovery path writes its own snapshot
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
        // A configuration/dependency refusal is Error with guidance; IsDegraded is reserved
        // for active session states and a failed save is reported via SaveFailed (R-F).
        Publish(new MonitorState(GuardianStatus.Error, false, reason, saveFailed));
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

    private async Task<AutostartResult?> SyncAutostartAsync(bool desiredEnabled, CancellationToken ct)
    {
        try
        {
            // The service reports a typed, verified state; the controller surfaces it instead of
            // swallowing it so the UI can show the failure and the checkbox reflects reality.
            var state = await _autostart.ReadStateAsync(ct).ConfigureAwait(false);
            if (state == AutostartState.Unknown)
            {
                // A reliable read failure must never be treated as "off" (2.4/2.5): mutating on
                // the back of an unknown state could destroy a mis-detected registration or claim
                // a verified state we do not have. Surface the uncertainty and make no change —
                // an Unknown state is never a trigger for a potentially destructive disable.
                _logger.LogWarning("Autostart state could not be read reliably; no change was made");
                return AutostartResult.Failure(
                    "the autostart registration state could not be verified; no change was made",
                    AutostartState.Unknown);
            }

            // Verified idempotent fast path.
            if (desiredEnabled && state == AutostartState.Enabled)
                return AutostartResult.Success(state);
            if (!desiredEnabled && state == AutostartState.Absent)
                return AutostartResult.Success(state);

            return await _autostart.SetEnabledAsync(desiredEnabled, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A service-side exception is surfaced as a failure result with diagnostics, never lost.
            _logger.LogError(ex, "Autostart sync failed");
            return AutostartResult.Failure($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // Publishes the verified autostart state + the latest sync error to the presentation layer
    // (the checkbox is driven by the actual state; a startup-sync failure is surfaced, not logged).
    private void PublishAutostartState(AutostartState? state, string? error)
    {
        AutostartActualState = state;
        AutostartSyncError = error;
        try
        {
            AutostartStateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AutostartStateChanged subscriber threw");
        }
    }

    private static SessionSnapshot CreateSessionSnapshot(string canonicalPath, string pin, ProcessTarget target) =>
        new(canonicalPath, target.NameWithoutExtension, target.WorkingDirectory, pin);

    // ---------------------------------------------------------------------
    // Waits
    // ---------------------------------------------------------------------

    // Frequency-agnostic deadline helper (v2.8.1 §B): converts a duration into a monotonic
    // deadline offset in the TimeProvider's native timestamp scale. Raw ticks are never
    // cast into a TimeSpan — arithmetic always happens through GetTimestamp/GetElapsedTime,
    // so both the real clock and the virtual test clock ( TimeSpan-scaled ticks) behave
    // identically.
    private long DeadlineTicks(TimeSpan duration)
    {
        var ticks = (long)Math.Round(duration.TotalSeconds * _time.TimestampFrequency);
        return ticks > 0 ? ticks : 0;
    }

    // The only wait primitive of the session loop (v2.8.1): a token-linked, deadline-based
    // wait resolved through the injected TimeProvider, so tests drive virtual time
    // deterministically. Waits until the absolute monotonic deadline `dueTimestamp`;
    // very large deadlines are sliced so the wait stays cancellable. Returns false when
    // cancelled (Stop/Suspend), true when the deadline elapsed.
    private const int MaxWaitSlice = int.MaxValue;

    private async Task<bool> WaitUntilAsync(long dueTimestamp, CancellationToken ct)
    {
        while (true)
        {
            var now = _time.GetTimestamp();
            if (now >= dueTimestamp)
                return !ct.IsCancellationRequested;

            var remainingMs = _time.GetElapsedTime(now, dueTimestamp).TotalMilliseconds;
            var sliceMs = remainingMs > MaxWaitSlice ? MaxWaitSlice : (int)remainingMs;
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(sliceMs), _time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
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
