using ProcessGuardian.Domain.Enums;

namespace ProcessGuardian.Domain.Interfaces;

public interface IStatusReporter
{
    GuardianStatus Current { get; }
    void Report(GuardianStatus status);
    event EventHandler<GuardianStatus>? StatusChanged;
}
