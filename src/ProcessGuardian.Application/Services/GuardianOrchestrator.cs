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
    // by an explicit, fully-verified A/B resolution. While pending, ordinary autostart sync is
    // blocked (a pre-existing scheduler task is never touched by plain settings commits or a
    // start), the checkbox is locked, and the persisted AutostartEnabled is preserved.
    private bool _recoveryPending;

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
                Publish(new MonitorState(GuardianStatus.Error, false, ex.Guidance ?? "Configuration is unavailable", false));
                return;
            }

            if (init == ConfigurationInitializationStatus.Refused)
            {
                _logger.LogError("Configuration initialization refused; monitoring will not run");
                // IsDegraded=false: a refused config is never a degraded session (R-F).
                Publish(new MonitorState(GuardianStatus.Error, false,
                    "Configuration could not be initialized; monitoring is off", false));
                return;
            }

            if (init == ConfigurationInitializationStatus.ResetNoticePending)
            {
                // [recovery policy] Preserve the (possibly pre-existing) autostart task and
                // stay stopped until the user chooses A or B: no SyncAutostartAsync(false)
                // from the fresh defaults, no automatic session. The notice latch is cleared
                // only by an explicit resolution (ResolveRecoveryAsync), never here.
                _recoveryPending = true;
                _logger.LogWarning("Reset notice pending; autostart task left untouched, monitoring not started");
                Publish(new MonitorState(GuardianStatus.Stopped, false, null, false));
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
                // IsDegraded is reserved for active session states (R-F).
                Publish(new MonitorState(GuardianStatus.Error, false, ex.Guidance ?? "Configuration is unavailable", false));
                return;
            }

            _committed = CommittedSettingsSnapshot.From(doc);
            _committedTargetPath = doc.TargetProcessPath;
            _latestPin = doc.TargetHashSha256;

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
            var sessionActive = SessionActive;

            if (_recoveryPending)
            {
                // [recovery policy] While a reset notice is pending, an ordinary settings commit
                // must NOT enable/disable autostart (the pre-existing task is never touched by a
                // plain save) and must NOT overwrite the persisted AutostartEnabled with a value
                // from a UI snapshot: only an explicit A/B resolution may change the flag. Other
                // settings are still saved normally, and a committed path change still clears the
                // pin (which is exactly what re-selecting the target must do).
                _committed = snapshot;
                var pendingNewTarget = string.IsNullOrWhiteSpace(targetPath) ? string.Empty : targetPath;
                var pendingPathChanged =
                    !string.Equals(pendingNewTarget, _committedTargetPath, StringComparison.OrdinalIgnoreCase);
                if (!sessionActive && pendingPathChanged)
                {
                    _committedTargetPath = pendingNewTarget;
                    _latestPin = string.Empty;
                }

                var pendingSaved = await PersistEnabledAsync(enabled: sessionActive, ct, applyAutostart: false)
                    .ConfigureAwait(false);
                if (!sessionActive && pendingSaved)
                    Publish(new MonitorState(GuardianStatus.Stopped, false, null, false));

                // Autostart was intentionally not attempted while the notice is pending.
                return pendingSaved
                    ? CommitSettingsResult.Persisted(autostart: null)
                    : CommitSettingsResult.SaveFailed();
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

            var saved = await PersistEnabledAsync(enabled: sessionActive, ct).ConfigureAwait(false);
            if (!sessionActive && saved)
                Publish(new MonitorState(GuardianStatus.Stopped, false, null, false));

            // The autostart result is surfaced with the commit so a registration failure is
            // never masked by a successful settings save (the UI shows the error explicitly).
            var autostart = await SyncAutostartAsync(snapshot.AutostartEnabled, ct).ConfigureAwait(false);
            return saved
                ? CommitSettingsResult.Persisted(autostart)
                : CommitSettingsResult.SaveFailed();
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <summary>True when the controller holds a recorded target pin (set on a Start after the reset).</summary>
    public bool HasRecordedPin => !string.IsNullOrWhiteSpace(_latestPin);

    public async Task<RecoveryResolutionResult> ResolveRecoveryAsync(
        RecoveryResolutionChoice choice,
        CommittedSettingsSnapshot? snapshot = null,
        string? targetPath = null,
        CancellationToken ct = default)
    {
        await _operationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            switch (choice)
            {
                case RecoveryResolutionChoice.KeepAutostart:
                    return await ResolveKeepAutostartAsync(snapshot, targetPath, ct).ConfigureAwait(false);
                case RecoveryResolutionChoice.RemoveAutostart:
                    return await ResolveRemoveAutostartAsync(snapshot, ct).ConfigureAwait(false);
                default:
                    _logger.LogError("Unknown recovery resolution choice {Choice}", choice);
                    return RecoveryResolutionResult.Failed("an unknown recovery option was requested");
            }
        }
        finally
        {
            _operationLock.Release();
        }
    }

    // Option A: keep the (possibly pre-existing) task. Allowed only after an explicit target
    // re-select + re-pin + successful save of that selection; an out-of-date pin or a stale
    // target path is rejected so the operation is idempotent and never clears the notice on a
    // partial success.
    private async Task<RecoveryResolutionResult> ResolveKeepAutostartAsync(
        CommittedSettingsSnapshot? snapshot, string? targetPath, CancellationToken ct)
    {
        // The user must have re-selected and re-pinned the target through a Start, which is the
        // only path that records a pin. Without a recorded pin, keep-autostart is not offered.
        if (!HasRecordedPin)
        {
            _logger.LogWarning("Keep-autostart rejected: no recorded target pin (must re-select and re-pin first)");
            return RecoveryResolutionResult.Failed(
                "re-select and pin the target first; the target has no recorded pin.");
        }

        if (snapshot is null)
        {
            _logger.LogWarning("Keep-autostart rejected: no committed settings supplied");
            return RecoveryResolutionResult.Failed("settings are unavailable; re-save your settings first.");
        }

        // The committed path+pin must be consistent: a staleness mismatch means the user's
        // pinned selection was not what we are about to persist, so refuse (idempotent fail).
        if (!string.IsNullOrWhiteSpace(targetPath)
            && !string.Equals(targetPath, _committedTargetPath, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Keep-autostart rejected: target path mismatch with the recorded pin");
            return RecoveryResolutionResult.Failed(
                "the selected target does not match the recorded pin; re-select and re-pin the target.");
        }

        // 1) Enable autostart first and verify its actual state.
        var enabled = await SyncAutostartAsync(true, ct).ConfigureAwait(false);
        if (enabled is null || !enabled.Value.Succeeded)
        {
            _logger.LogError("Recovery resolution (Keep autostart) failed: autostart could not be enabled: {Error}",
                enabled?.ErrorMessage ?? "not attempted");
            return RecoveryResolutionResult.Failed(
                $"autostart could not be enabled: {enabled?.ErrorMessage ?? "unknown reason"}");
        }

        PublishAutostartState(AutostartState.Enabled, null);

        // 2) Persist the committed settings with AutostartEnabled = true.
        _committed = snapshot with { AutostartEnabled = true };
        bool saved;
        try
        {
            saved = await PersistEnabledAsync(enabled: SessionActive, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Keep-autostart: persisting settings failed");
            return RecoveryResolutionResult.Failed($"settings could not be saved: {ex.Message}");
        }

        if (!saved)
        {
            _logger.LogError("Keep-autostart: settings save failed");
            return RecoveryResolutionResult.Failed("settings could not be saved; try again.");
        }

        // 3) Only a fully-verified operation clears the notice; a failure anywhere above leaves
        // it pending so the user can retry.
        return await TryClearResetNoticeAsync("Keep autostart").ConfigureAwait(false);
    }

    // Option B: remove the (possibly pre-existing) task and keep AutostartEnabled = false.
    private async Task<RecoveryResolutionResult> ResolveRemoveAutostartAsync(
        CommittedSettingsSnapshot? snapshot, CancellationToken ct)
    {
        // 1) Disable autostart first and verify its actual state (task actually absent).
        var disabled = await SyncAutostartAsync(false, ct).ConfigureAwait(false);
        if (disabled is null || !disabled.Value.Succeeded)
        {
            _logger.LogError("Recovery resolution (Remove autostart) failed: autostart could not be removed: {Error}",
                disabled?.ErrorMessage ?? "not attempted");
            return RecoveryResolutionResult.Failed(
                $"autostart could not be removed: {disabled?.ErrorMessage ?? "unknown reason"}");
        }

        PublishAutostartState(AutostartState.Absent, null);

        // 2) Persist AutostartEnabled = false. Monitoring state is untouched by option B.
        if (snapshot is not null)
            _committed = snapshot with { AutostartEnabled = false };
        bool saved;
        try
        {
            saved = await PersistEnabledAsync(enabled: SessionActive, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Remove-autostart: persisting settings failed");
            return RecoveryResolutionResult.Failed($"settings could not be saved: {ex.Message}");
        }

        if (!saved)
        {
            _logger.LogError("Remove-autostart: settings save failed");
            return RecoveryResolutionResult.Failed("settings could not be saved; try again.");
        }

        // 3) Only a fully-verified operation clears the notice.
        return await TryClearResetNoticeAsync("Remove autostart").ConfigureAwait(false);
    }

    private async Task<RecoveryResolutionResult> TryClearResetNoticeAsync(string variant)
    {
        if (_resetNoticeAck is null)
        {
            _logger.LogError("Recovery resolution ({Variant}) failed: reset-notice acknowledgement is unavailable", variant);
            return RecoveryResolutionResult.Failed("the recovery could not be finalized; try again later.");
        }

        try
        {
            await _resetNoticeAck.ClearAsync(CancellationToken.None).ConfigureAwait(false);
            _logger.LogInformation("Recovery resolution ({Variant}) completed; reset notice cleared", variant);
            // The notice is gone: ordinary autostart sync is allowed again.
            _recoveryPending = false;
            Publish(new MonitorState(GuardianStatus.Stopped, false, null, false));
            return RecoveryResolutionResult.Succeeded();
        }
        catch (Exception ex)
        {
            // The latch could not be cleared, so the recovery is not complete: leave the notice
            // pending (and ordinary sync blocked) and let the user retry rather than reporting a
            // false success.
            _logger.LogError(ex, "Recovery resolution ({Variant}) could not finalize the reset notice", variant);
            return RecoveryResolutionResult.Failed("the recovery could not be finalized; try again.");
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
            if (latestCommittedSettings is not null)
                _committed = latestCommittedSettings;

            var cts = _sessionCts;
            var task = _sessionTask;
            if (cts is null || task is { IsCompleted: true })
            {
                // No live session: persist the desired off-state (best-effort) and report stopped.
                var saved = await PersistEnabledAsync(enabled: false, ct, applyAutostart: !_recoveryPending).ConfigureAwait(false);
                Publish(new MonitorState(GuardianStatus.Stopped, !saved, null, !saved));
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

        // Published outside the session task (R-F): the loop already ran to completion, so a
        // new Start is already allowed; this guard keeps a stale fatal Error from being
        // published over a session that has since begun.
        if (!ReferenceEquals(_sessionTask, sessionTask))
            return;

        // Persist enabled=false best-effort, then Error — a failed save never turns the
        // terminal state into degraded, it is surfaced via the independent SaveFailed flag.
        await TryPersistDisabledBestEffortAsync(_committed).ConfigureAwait(false);
        Publish(new MonitorState(GuardianStatus.Error, false, fatalReason, _lastPersistFailed));
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
                doc.AutostartEnabled = preservedAutostart; // never overwrite the flag outside an explicit A/B resolution
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
