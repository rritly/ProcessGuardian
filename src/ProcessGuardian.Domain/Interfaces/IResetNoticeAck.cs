namespace ProcessGuardian.Domain.Interfaces;

/// <summary>
/// Clears the persistent reset-notice latch after the controller-owned automatic recovery
/// has been fully verified (task absent + disabled settings persisted + journal cleared,
/// v2.5 R-AS-AUTO). Lives in Domain so the Application controller does not depend on the
/// Infrastructure HKLM journal; the Infrastructure implementation wraps the journal's
/// "write RecoveryState.None" transition. This is the only way the notice may be cleared —
/// never on plain app start, never on re-select/re-pin alone. A failed clear keeps the
/// journal pending so the next startup re-runs the idempotent recovery. The journal is not
/// a settings writer and stays out of the controller's ordinary persistence path.
/// </summary>
public interface IResetNoticeAck
{
    Task ClearAsync(CancellationToken ct = default);
}
