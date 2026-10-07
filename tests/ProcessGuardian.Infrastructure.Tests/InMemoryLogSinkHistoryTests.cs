using System.IO;
using System.Linq;
using FluentAssertions;
using ProcessGuardian.Infrastructure.Logging;
using Serilog.Events;
using Serilog.Parsing;
using Xunit;

namespace ProcessGuardian.Infrastructure.Tests;

// Startup-history tests: the log card must show the previous session's entries
// from logs.log immediately, without duplicating them back into the file.
public class InMemoryLogSinkHistoryTests : IDisposable
{
    private readonly string _dir;

    public InMemoryLogSinkHistoryTests()
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
    public void Constructor_SeedsRecentLogsFromFileTail()
    {
        var path = Path.Combine(_dir, "logs.log");
        File.WriteAllLines(path, Enumerable.Range(0, 300)
            .Select(i => $"2026-04-10 19:20:42 INFO  file line {i}"));

        using var sink = new InMemoryLogSink(path);

        var recent = sink.GetRecentLogs(200).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        recent.Should().HaveCount(200);
        recent[0].Should().Contain("file line 100");
        recent[^1].Should().Contain("file line 299");
    }

    [Fact]
    public void Restart_OldEntriesVisibleInBuffer_AndNewOnesAppend()
    {
        var path = Path.Combine(_dir, "logs.log");
        using var first = new InMemoryLogSink(path);
        first.Emit(CreateEvent("old session entry"));
        first.Flush();

        using var second = new InMemoryLogSink(path);
        second.GetRecentLogs(200).Should().Contain("old session entry");

        second.Emit(CreateEvent("new session entry"));
        second.GetRecentLogs(200).Should().Contain("new session entry");
        second.Flush();

        var text = File.ReadAllText(path);
        text.Should().Contain("old session entry");
        text.Should().Contain("new session entry");
    }

    [Fact]
    public void Flush_AfterSeed_DoesNotDuplicateFileLines()
    {
        var path = Path.Combine(_dir, "logs.log");
        File.WriteAllLines(path, new[] { "2026-04-10 19:20:42 INFO  seeded entry" });
        var before = File.ReadAllText(path);

        using var sink = new InMemoryLogSink(path);
        sink.Flush();

        File.ReadAllText(path).Should().Be(before);
    }
}
