using Microsoft.Extensions.Logging;
using ProcessGuardian.Domain.Enums;
using ProcessGuardian.Domain.Interfaces;

namespace ProcessGuardian.Infrastructure.Status;

public sealed class StatusReporter : IStatusReporter
{
    private readonly ILogger<StatusReporter>? _logger;

    // Single constructor: DI supplies the logger via AddLogging; tests may pass null.
    public StatusReporter(ILogger<StatusReporter>? logger = null)
    {
        _logger = logger;
    }

    public GuardianStatus Current { get; private set; } = GuardianStatus.Stopped;

    public void Report(GuardianStatus status)
    {
        Current = status;
        try
        {
            StatusChanged?.Invoke(this, status);
        }
        catch (Exception ex)
        {
            // Event handlers should not throw, but if they do, don't crash the
            // reporter; make the failure visible when a logger is available.
            if (_logger is not null)
                _logger.LogError(ex, "StatusChanged subscriber threw");
        }
    }

    public event EventHandler<GuardianStatus>? StatusChanged;
}
