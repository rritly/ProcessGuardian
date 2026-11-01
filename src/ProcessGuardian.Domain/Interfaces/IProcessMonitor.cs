using ProcessGuardian.Domain.ValueObjects;

namespace ProcessGuardian.Domain.Interfaces;

public interface IProcessMonitor
{
    Task<ProcessCheckResult> CheckAsync(ProcessTarget target, CancellationToken ct);

    // Synchronous process-creation call: the launch boundary must complete synchronously so the
    // controller can hold its launch gate across exactly this call and nothing else (R-CD).
    ProcessStartResult TryStart(ProcessTarget target, CancellationToken ct);

    Task<bool> VerifyStartupAsync(ProcessTarget target, TimeSpan timeout, CancellationToken ct);
}
