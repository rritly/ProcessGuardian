using ProcessGuardian.Domain.Enums;
using ProcessGuardian.Domain.Interfaces;

namespace ProcessGuardian.Infrastructure.Status;

public sealed class StatusReporter : IStatusReporter
{
    public GuardianStatus Current { get; private set; } = GuardianStatus.Stopped;

    public void Report(GuardianStatus status)
    {
        Current = status;
        try
        {
            StatusChanged?.Invoke(this, status);
        }
        catch (Exception)
        {
            // Event handlers should not throw, but if they do, don't crash the reporter
        }
    }

    public event EventHandler<GuardianStatus>? StatusChanged;
}
