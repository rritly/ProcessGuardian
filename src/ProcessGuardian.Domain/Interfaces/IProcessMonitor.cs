using ProcessGuardian.Domain.ValueObjects;

namespace ProcessGuardian.Domain.Interfaces;

public interface IProcessMonitor
{
    Task<ProcessCheckResult> CheckAsync(ProcessTarget target, CancellationToken ct);
    Task<bool> StartAsync(ProcessTarget target, CancellationToken ct);
    Task<bool> VerifyStartupAsync(ProcessTarget target, TimeSpan timeout, CancellationToken ct);
}
