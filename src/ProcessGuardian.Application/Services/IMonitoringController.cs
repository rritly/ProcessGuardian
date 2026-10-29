using ProcessGuardian.Domain.Entities;

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
    Task<bool> CommitSettingsAsync(CommittedSettingsSnapshot snapshot, string? targetPath, CancellationToken ct = default);

    /// <summary>Starts a monitoring session: validates the path, computes the pin,
    /// persists canonical path + pin + enabled together before the session starts.</summary>
    Task<StartResult> StartAsync(StartRequest request, CancellationToken ct = default);

    /// <summary>Cancels the session and persists <c>enabled=false</c>. Returns a typed
    /// result with separate runtime/durable guarantees.</summary>
    Task<StopResult> StopAsync(CommittedSettingsSnapshot? latestCommittedSettings = null, CancellationToken ct = default);

    /// <summary>Cancels the session synchronously without persisting the enabled flag
    /// or any UI snapshot (consumed by Windows SessionEnding).</summary>
    Task SuspendAsync(CancellationToken ct = default);
}
