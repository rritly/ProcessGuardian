namespace ProcessGuardian.Domain.Entities;

/// <summary>
/// Immutable committed UI-editable settings, frozen at the point a snapshot is taken.
/// This is what the ViewModel supplies to the controller for persistence. It never
/// carries a target-path draft that is not yet committed, never carries the
/// controller-owned <c>TargetHashSha256</c>, and never carries <c>IsMonitoringEnabled</c>
/// — the controller owns the enabled flag and the pin.
/// </summary>
public sealed record CommittedSettingsSnapshot(
    int CheckIntervalSeconds,
    int InitialDelaySeconds,
    int MaxRestartAttempts,
    int RestartDelaySeconds,
    int StartupVerificationTimeoutSeconds,
    bool AutostartEnabled,
    bool EnableToasts)
{
    /// <summary>Captures the committed UI-editable fields of a settings document.</summary>
    public static CommittedSettingsSnapshot From(AppSettings s) => new(
        s.CheckIntervalSeconds,
        s.InitialDelaySeconds,
        s.MaxRestartAttempts,
        s.RestartDelaySeconds,
        s.StartupVerificationTimeoutSeconds,
        s.AutostartEnabled,
        s.EnableToasts);
}
