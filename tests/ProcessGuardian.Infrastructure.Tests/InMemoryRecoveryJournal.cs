using System.IO;
using ProcessGuardian.Infrastructure.Recovery;

namespace ProcessGuardian.Infrastructure.Tests;

/// <summary>
/// In-memory recovery journal for CI: forces "a crash at each step" of the elevated
/// quarantine-and-recreate protocol without needing an elevated runner or real HKLM.
/// </summary>
internal sealed class InMemoryRecoveryJournal : IElevatedRecoveryJournal
{
    public RecoveryState State { get; private set; }

    /// <summary>Invoked before a write commits; return true to simulate a crash at that transition.</summary>
    public Func<RecoveryState, bool>? ShouldCrash { get; set; }

    public List<RecoveryState> Writes { get; } = new();

    public Task<RecoveryState> ReadAsync(CancellationToken ct = default)
        => Task.FromResult(State);

    public Task WriteAsync(RecoveryState state, CancellationToken ct = default)
    {
        if (ShouldCrash?.Invoke(state) == true)
            throw new IOException("scripted journal crash");
        Writes.Add(state);
        State = state;
        return Task.CompletedTask;
    }
}
