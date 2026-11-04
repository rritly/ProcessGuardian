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
                // persisted or synced. Publish a clean Stopped state so the UI stays coherent.
                _logger.LogWarning("Stop: configuration initialization was refused; no persistence attempted");
                Publish(new MonitorState(GuardianStatus.Stopped, false, null, false));
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
                Publish(new MonitorState(GuardianStatus.Stopped, false, null, false));
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
            SessionEnd.BudgetExhausted => "monitoring stopped after repeated failed recovery attempts",
            _ => null,
        };
        if (fatalReason is null)
            return;

        // [R-F settlement sync] Settle under the operation lock, with the liveness re-check inside
        // the lock: Stop/Suspend tear the session holder down and a new Start replaces it, so a
        // stale fatal continuation — one that was already in flight past its first await while the
        // persist ran — observes a stale reference and skips both the persist and the publish.
        // Serializing the persist-and-publish with Start/Stop means a Stop during the settlement
        // either wins the lock first (holder nulled -> this continuation skips) or runs after it
        // and publishes Stopped last; either way a stale Error is never the final published state,
        // and a late IsMonitoringEnabled=false / AutostartEnabled=false cannot disable a newer
        // session whose Start is linearized after (or re-persists over) this settlement.
        await _operationLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(_sessionTask, sessionTask))
                return;

            // Persist enabled=false best-effort, then Error — a failed save never turns the
            // terminal state into degraded, it is surfaced via the independent SaveFailed flag.
            await TryPersistDisabledBestEffortAsync(_committed).ConfigureAwait(false);
            Publish(new MonitorState(GuardianStatus.Error, false, fatalReason, _lastPersistFailed));
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task<SessionEnd> RunSessionAsync(SessionSnapshot session, CancellationToken ct)
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
                    return SessionEnd.Cancelled;
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
                        if (outcome != SessionEnd.Continue)
                            return outcome;
                    }
                }
                catch (OperationCanceledException)
                {
                    return SessionEnd.Cancelled;
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
                    return SessionEnd.Cancelled;
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
        /// <summary>Fatal: the consecutive restart budget was exhausted.</summary>
        BudgetExhausted,
    }

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

    private async Task<SessionEnd> HandleMissingAsync(
        SessionSnapshot session, ProcessTarget target,
        SessionCounters counters,
        CommittedSettingsSnapshot knobs, CancellationToken ct)
    {
        if (counters.InitialLaunchPending)
        {
            _logger.LogInformation("Initial start for {Target}", target.NameWithoutExtension);
            var outcome = await StartAndVerifyAsync(target, knobs, showRecoveryToast: false, ct).ConfigureAwait(false);
            if (outcome == StartOutcome.SessionEnded || outcome == StartOutcome.Blocked)
                return SessionEnd.Cancelled;
            if (outcome == StartOutcome.Mismatch)
                return SessionEnd.HashMismatch;
            // A degraded (missing/unreadable) free launch spends no budget, does not consume the
            // free launch, and feeds the degraded indicator (E): keep initial-pending, mark degraded.
            if (outcome == StartOutcome.Degraded)
            {
                MarkDegraded(counters);
                return SessionEnd.Continue;
            }
            counters.InitialLaunchPending = false;
            if (outcome == StartOutcome.Verified)
                ResetDegraded(counters);
            return SessionEnd.Continue;
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

            // Fatal: the budget is spent. The enabled=false persistence and the Error publish
            // happen outside the session task (SettleSessionOutcomeAsync), so the loop may not
            // await a store write and a new Start stays blocked only until this task completes (R-F).
            return SessionEnd.BudgetExhausted;
        }

        Publish(new MonitorState(GuardianStatus.Restarting, false, null, false));
        _logger.LogInformation("Restart attempt for {Target}", target.NameWithoutExtension);

        var started = await StartAndVerifyAsync(target, knobs, showRecoveryToast: true, ct).ConfigureAwait(false);
        if (started == StartOutcome.SessionEnded || started == StartOutcome.Blocked)
            return SessionEnd.Cancelled;
        if (started == StartOutcome.Mismatch)
            return SessionEnd.HashMismatch;
        if (started == StartOutcome.Degraded)
        {
            // A degraded (missing/unreadable) relaunch spends no budget and sleeps no restart
            // delay; the session keeps polling and feeds the degraded indicator. Only a genuine
            // launch that ran and failed advances the consecutive counter (budget-charge rule, E).
            MarkDegraded(counters);
            return SessionEnd.Continue;
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
                return SessionEnd.Cancelled;
            }
        }

        return SessionEnd.Continue;
    }

    private enum StartOutcome { NotStarted, Verified, VerifyFailed, Blocked, Degraded, SessionEnded, Mismatch }

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
                // Fail closed (K): hash mismatch is fatal. The enabled=false persistence and the
                // Error publish are deferred to the outside-the-task continuation
                // (SettleSessionOutcomeAsync, R-F), so this method only returns the terminal
                // outcome and never publishes Error from inside the session task.
                _logger.LogError("Target {Target} no longer matches its recorded pin; terminating the session",
                    target.NameWithoutExtension);
                return StartOutcome.Mismatch;
        }

        // Linearized launch (R-CD): only the final session/gate check and the synchronous
        // Process.Start run while _launchGate is held. The gate is released immediately after
        // the process-creation result is captured — startup verification, toasts and delays all
        // run outside it and remain cancellable by the session token. Stop/Suspend close the
        // gate and cancel the session in one brief critical section, then settle it outside the
        // gate, so no launch begins after they act and none begins after a completed Stop
        // returns.
        if (BeforeLaunchGateHook is not null)
            await BeforeLaunchGateHook(CurrentSessionOr(target), ct).ConfigureAwait(false);

        await _launchGate.WaitAsync(ct).ConfigureAwait(false);
        ProcessStartResult start;
        try
        {
            // Re-check session identity under the gate; no await may precede the sync launch.
            if (ct.IsCancellationRequested || _launchGateClosed)
                return StartOutcome.SessionEnded;

            InsideLaunchGateHook?.Invoke();

            start = _monitor.TryStart(target, ct);
        }
        catch (OperationCanceledException)
        {
            return StartOutcome.SessionEnded;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TryStart threw for {Target}", target.NameWithoutExtension);
            return StartOutcome.NotStarted;
        }
        finally
        {
            // Released immediately after the start result is captured (R-CD); never held across
            // an await, so verification below cannot block Stop for the verification timeout.
            _launchGate.Release();
        }

        switch (start.Status)
        {
            case ProcessStartStatus.Cancelled:
                return StartOutcome.SessionEnded;
            case ProcessStartStatus.Missing:
                _logger.LogWarning("Target {Target} disappeared before launch; skipping (degraded), no budget spent",
                    target.NameWithoutExtension);
                return StartOutcome.Degraded;
            case ProcessStartStatus.ReadError:
                _logger.LogWarning("Target {Target} became unreadable before launch; skipping (degraded), no budget spent",
                    target.NameWithoutExtension);
                return StartOutcome.Degraded;
            case ProcessStartStatus.Failed:
                _logger.LogError("Failed to start process {Target}: {Error}", target.NameWithoutExtension, start.ErrorMessage);
                return StartOutcome.NotStarted;
        }
        // Started: proceed to startup verification below (outside the gate).

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
