namespace ProcessGuardian.Domain.Enums;

/// <summary>
/// The two explicit user choices that resolve a pending reset notice (A's recovery
/// policy). No automatic resolution exists: the user must pick one, and neither the
/// scheduler task nor <c>AutostartEnabled</c> is touched until that explicit choice.
/// </summary>
public enum RecoveryResolutionChoice
{
    /// <summary>
    /// Keep the (possibly pre-existing) scheduler task and re-enable autostart. Requires an
    /// explicit target re-selection/re-pin and a verified autostart enable before the notice
    /// is cleared.
    /// </summary>
    KeepAutostart,

    /// <summary>
    /// Remove the scheduler task and keep <c>AutostartEnabled=false</c>. Requires a verified
    /// task removal and a persisted disabled flag before the notice is cleared.
    /// </summary>
    RemoveAutostart,
}
