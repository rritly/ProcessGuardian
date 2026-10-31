namespace ProcessGuardian.Domain.Interfaces;

/// <summary>
/// Clears the persistent reset-notice latch after the user has explicitly completed one
/// of the two recovery resolutions. Lives in Domain so the Application controller does
/// not depend on the Infrastructure HKLM journal; the Infrastructure implementation wraps
/// the journal's "write RecoveryState.None" transition. This is the only way the notice
/// may be cleared — never on plain app start, never on re-select/re-pin alone (A). The
/// journal is not a settings writer and stays out of the controller's ordinary
/// persistence path.
/// </summary>
public interface IResetNoticeAck
{
    Task ClearAsync(CancellationToken ct = default);
}
