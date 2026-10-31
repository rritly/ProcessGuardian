using ProcessGuardian.Domain.Entities;
using ProcessGuardian.Domain.Enums;

namespace ProcessGuardian.Application.Services;
/// <summary>
/// Single writer for monitoring state and persistence. The controller serializes
/// Start/Stop/Suspend internally (the UI busy gate is no longer the only guard),
/// owns the per-session cancellation, linearizes <c>Process.Start</c> against Stop
/// through a launch gate, computes and persists the target pin, performs every
/// settings save by merging committed UI fields with controller-owned state, and
/// publishes a <see cref="MonitorState"/> channel the ViewModel projects onto
/// <c>GuardianStatus</c> + <c>IsDegraded</c> bindings. No caller may write the store
/// or autostart service directly.
/// </summary>
public interface IMonitoringController
{
    /// <summary>Latest published monitoring state (readable without subscribing).</summary>
    MonitorState CurrentState { get; }

    /// <summary>
    /// True when the controller holds a recorded target pin (computed and stored on a Start
    /// after the reset). Gates the keep-autostart recovery option: it must not be offered for
    /// execution until the user has re-selected and re-pinned the target.
    /// </summary>
    bool HasRecordedPin { get; }

    /// <summary>True while a reset notice is pending; ordinary autostart sync is blocked (recovery A/B).</summary>
    bool RecoveryPending { get; }

    /// <summary>Latest verified actual autostart registration state (drives the checkbox display).</summary>
    AutostartState? AutostartActualState { get; }

    /// <summary>Error from the most recent autostart sync (startup reconciliation included).</summary>
    string? AutostartSyncError { get; }

    /// <summary>Raised whenever the autostart actual state / sync error changes.</summary>
    event EventHandler? AutostartStateChanged;

    /// <summary>State channel; raised for every observable transition (including degraded/save-failed flags).</summary>
    event EventHandler<MonitorState>? StateChanged;

    /// <summary>Reconciles persisted state at startup and runs the pre-launch checks
    /// before any launch; unusable/weak config never auto-starts monitoring.</summary>
    Task ReconcileAsync(CancellationToken ct = default);

    /// <summary>Returns an independent copy of the current persisted settings (read-only
    /// view for the ViewModel's initial display; all writes go through the controller).</summary>
    Task<AppSettings> GetCurrentSettingsAsync(CancellationToken ct = default);

    /// <summary>Persists committed UI settings, ignoring a target-path change while a
    /// session is active (path change is effective only in the stopped state). Clears
    /// the pin on a committed path change in the stopped state.</summary>
    Task<CommitSettingsResult> CommitSettingsAsync(CommittedSettingsSnapshot snapshot, string? targetPath, CancellationToken ct = default);

    /// <summary>Starts a monitoring session: validates the path, computes the pin,
    /// persists canonical path + pin + enabled together before the session starts.</summary>
    Task<StartResult> StartAsync(StartRequest request, CancellationToken ct = default);

    /// <summary>Cancels the session and persists <c>enabled=false</c>. Returns a typed
    /// result with separate runtime/durable guarantees.</summary>
    Task<StopResult> StopAsync(CommittedSettingsSnapshot? latestCommittedSettings = null, CancellationToken ct = default);

    /// <summary>Cancels the session synchronously without persisting the enabled flag
    /// or any UI snapshot (consumed by Windows SessionEnding).</summary>
    Task SuspendAsync(CancellationToken ct = default);

    /// <summary>
    /// Explicitly resolves a pending reset notice through one of the two recovery options
    /// (see <see cref="RecoveryResolutionChoice"/>). No automatic resolution exists: the
    /// notice is cleared only after the chosen option's operations succeeded and were
    /// verified (never on plain start, never on re-select/re-pin alone). Returns
    /// <see cref="RecoveryResolutionResult.Resolved"/> false with the notice still pending
    /// on any failure, so the user can retry.
    /// </summary>
    Task<RecoveryResolutionResult> ResolveRecoveryAsync(
        RecoveryResolutionChoice choice,
        CommittedSettingsSnapshot? snapshot = null,
        string? targetPath = null,
        CancellationToken ct = default);
}
