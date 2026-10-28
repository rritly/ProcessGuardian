using ProcessGuardian.Domain.Enums;

namespace ProcessGuardian.Domain.Interfaces;

public interface ITrayService : IDisposable
{
    void Initialize();
    void SetStatus(GuardianStatus status);

    /// <summary>
    /// Sets a persistent user-facing notice in the tray tooltip (reset-now notice or config
    /// refusal guidance). Pass <c>null</c>/empty to restore the standard "Name - status" tooltip.
    /// </summary>
    void SetNotice(string? message);

    event EventHandler? ShowRequested;
    event EventHandler? StartRequested;
    event EventHandler? StopRequested;
    event EventHandler? ExitRequested;
}
