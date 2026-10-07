using System.IO;
using FluentAssertions;
using ProcessGuardian.Infrastructure.Logging;
using Serilog.Events;
using Serilog.Parsing;
using Xunit;

namespace ProcessGuardian.Infrastructure.Tests;

// Sinks run against throwaway files under %TEMP%: tests must never touch the
// real %LocalAppData% log (the default constructor path).
public class InMemoryLogSinkTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ProcessGuardian.Tests", Guid.NewGuid().ToString("N"));

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

    private InMemoryLogSink CreateSink() => new(Path.Combine(_dir, "logs.log"));

    private static LogEvent CreateEvent(string message, LogEventLevel level = LogEventLevel.Information)
        => new(
            new DateTimeOffset(new DateTime(2026, 4, 10, 19, 20, 42, DateTimeKind.Local)),
            level,
            exception: null,
            messageTemplate: new MessageTemplateParser().Parse(message),
            properties: Enumerable.Empty<LogEventProperty>());

    [Fact]
    public void Emit_601Messages_RingCapsAt600_GetRecentLogsReturnsLast200()
    {
        using var sink = CreateSink();

        for (int i = 0; i < 601; i++)
            sink.Emit(CreateEvent($"message {i}", LogEventLevel.Information));

        var lines = sink.GetRecentLogs(200).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(200);
        lines[0].Should().Contain("message 401");
        lines[^1].Should().Contain("message 600");
        lines.Should().NotContain(l => l.Contains("message 0"));
        lines.Should().NotContain(l => l.Contains("message 400"));
    }

    [Fact]
    public void Emit_Format_HasNoBrackets_InfoLevel_TwoSpacesAfterLevel()
    {
        using var sink = CreateSink();
        sink.Emit(CreateEvent("Monitoring enabled for notepad"));

        var lines = sink.GetRecentLogs(1).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(1);
        lines[0].Should().NotContain("[");
        lines[0].Should().Contain("INFO");
        lines[0].Should().Match("2026-04-10 19:20:42 INFO  *");
    }

    [Fact]
    public void Emit_ErrorLevel_MapsToErr_TwoSpacesAfterLevel()
    {
        using var sink = CreateSink();
        sink.Emit(CreateEvent("something failed", LogEventLevel.Error));

        var lines = sink.GetRecentLogs(1).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        lines[0].Should().Contain("ERR");
        lines[0].Should().Match("2026-04-10 19:20:42 ERR  *");
    }

    [Fact]
    public void Clear_EmptiesBuffer_AndFile_GetRecentLogsEmpty()
    {
        using var sink = CreateSink();

        for (int i = 0; i < 10; i++)
            sink.Emit(CreateEvent($"message {i}"));

        sink.GetRecentLogs(200).Should().NotBeEmpty();

        sink.Clear();

        sink.GetRecentLogs(200).Should().BeEmpty();
        File.Exists(sink.LogFilePath).Should().BeTrue();
        File.ReadAllText(sink.LogFilePath).Should().BeEmpty();
    }
}
