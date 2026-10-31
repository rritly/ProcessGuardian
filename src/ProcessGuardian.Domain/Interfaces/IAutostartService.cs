namespace ProcessGuardian.Domain.Interfaces;

/// <summary>
/// Edition-scoped autostart registration. The regular edition uses an HKCU Run entry; the
/// elevated edition uses a scheduled task because HKCU Run cannot auto-elevate (the DI
/// wiring in <c>Bootstrapper</c> selects the implementation per edition). <c>SetEnabledAsync</c>
/// returns a typed result (<see cref="AutostartResult"/>) whose <c>Succeeded</c> reflects a
/// verified actual state, never just an exit code or the absence of an exception.
/// </summary>
public interface IAutostartService
{
    /// <summary>Reports whether autostart is currently registered.</summary>
    Task<bool> IsEnabledAsync(CancellationToken ct = default);

    /// <summary>
    /// Enables or disables autostart. The returned result verifies the actual registration
    /// state against the requested state before reporting success; a nonzero
    /// <c>schtasks.exe</c> exit code, a process-start failure, or a state that did not take
    /// effect are all reported as failures with diagnostics.
    /// </summary>
    Task<AutostartResult> SetEnabledAsync(bool enabled, CancellationToken ct = default);
}
