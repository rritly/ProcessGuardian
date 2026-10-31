using Microsoft.Extensions.Logging;
using ProcessGuardian.Domain.Interfaces;

namespace ProcessGuardian.Infrastructure.Recovery;

/// <summary>
/// Clears the persistent reset-notice latch through the HKLM recovery journal. This is the
/// Infrastructure-side implementation of <see cref="IResetNoticeAck"/> the Application
/// controller consumes during recovery resolution A/B. It is registered per the same
/// journal instance the elevated store uses, so clearing is observed by both.
/// </summary>
public sealed class RecoveryResetNoticeAck : IResetNoticeAck
{
    private readonly IElevatedRecoveryJournal _journal;
    private readonly ILogger<RecoveryResetNoticeAck> _logger;

    public RecoveryResetNoticeAck(IElevatedRecoveryJournal journal, ILogger<RecoveryResetNoticeAck> logger)
    {
        _journal = journal;
        _logger = logger;
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        // A failure here is deliberately propagated (not swallowed): the caller must not
        // report a completed recovery resolution if the notice could not actually be cleared.
        await _journal.WriteAsync(RecoveryState.None, ct).ConfigureAwait(false);
        _logger.LogInformation("Recovery reset notice cleared");
    }
}
