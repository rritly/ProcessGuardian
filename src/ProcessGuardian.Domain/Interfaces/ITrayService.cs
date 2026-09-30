using ProcessGuardian.Domain.Enums;

namespace ProcessGuardian.Domain.Interfaces;

public interface ITrayService : IDisposable
{
    void Initialize();
    void SetStatus(GuardianStatus status);
    event EventHandler? ShowRequested;
    event EventHandler? StartRequested;
    event EventHandler? StopRequested;
    event EventHandler? ExitRequested;
}
