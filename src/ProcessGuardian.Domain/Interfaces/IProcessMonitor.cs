using ProcessGuardian.Domain.ValueObjects;

namespace ProcessGuardian.Domain.Interfaces;

public interface IProcessMonitor
{
    Task<ProcessCheckResult> CheckAsync(ProcessTarget target, CancellationToken ct);

    // Synchronous process-creation call: the launch boundary must complete synchronously so the
    // controller can hold its launch gate across exactly this call and nothing else (R-CD).
    ProcessStartResult TryStart(ProcessTarget target, CancellationToken ct);

    // Startup appearance/stability verification (v2.8) is orchestrated by the controller
    // directly from typed CheckAsync results — a bool-only verification collapsed the
    // indeterminate Unknown state into a timeout failure.
}
