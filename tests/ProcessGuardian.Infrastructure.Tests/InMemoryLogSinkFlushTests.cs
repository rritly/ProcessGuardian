using System.IO;
using FluentAssertions;
using ProcessGuardian.Infrastructure.Logging;
using Serilog.Events;
using Serilog.Parsing;
using Xunit;

namespace ProcessGuardian.Infrastructure.Tests;

// Throttled-flush regression tests: Emit must not rewrite the file synchronously; the
// buffer reaches the disk via the flush timer or an explicit Flush().
public class InMemoryLogSinkFlushTests : IDisposable
{
    private readonly string _dir;

    public InMemoryLogSinkFlushTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ProcessGuardian.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup; never throw from Dispose
        }
    }

    private static LogEvent CreateEvent(string message)
        => new(
            new DateTimeOffset(new DateTime(2026, 4, 10, 19, 20, 42, DateTimeKind.Local)),
            LogEventLevel.Information,
            exception: null,
            messageTemplate: new MessageTemplateParser().Parse(message),
            properties: Enumerable.Empty<LogEventProperty>());

    [Fact]
    public void Emit_DoesNotWriteSynchronously_ThenFlushPersistsBuffer()
    {
        var path = Path.Combine(_dir, "logs.log");
        var sink = new InMemoryLogSink(path);

        sink.Emit(CreateEvent("persist me"));

        // The first timer tick is due 500 ms after construction; Emit must not have
        // written anything synchronously.
        File.Exists(path).Should().BeFalse();

        sink.Flush();

        File.ReadAllText(path).Should().Contain("persist me");
    }

    [Fact]
    public void Flush_WhenClean_IsNoOp()
    {
        var path = Path.Combine(_dir, "logs.log");
        var sink = new InMemoryLogSink(path);

        sink.Flush();

        // No pending entries -> nothing written; Flush must not create a file.
        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public void Clear_AfterFlush_EmptiesFileImmediately()
    {
        var path = Path.Combine(_dir, "logs.log");
        var sink = new InMemoryLogSink(path);

        sink.Emit(CreateEvent("entry one"));
        sink.Emit(CreateEvent("entry two"));
        sink.Flush();
        File.ReadAllText(path).Should().NotBeEmpty();

        sink.Clear();

        File.ReadAllText(path).Should().BeEmpty();
        sink.GetRecentLogs(200).Should().BeEmpty();
    }
}
