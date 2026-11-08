using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using ProcessGuardian.Domain.Enums;
using ProcessGuardian.Infrastructure.Autostart;
using ProcessGuardian.Infrastructure.Logging;
using Serilog;
using Serilog.Extensions.Logging;
using Xunit;

namespace ProcessGuardian.Wpf.Tests;

// R-AS-TASK-SCHEDULER-DIAGNOSTICS rendering test: the literal scalar formatting ({Operation:l},
// {StdOut:l}, ...) in ScheduledTaskAutostartService templates must render WITHOUT surrounding
// quote characters through the exact production rendering path — MEL message template -> Serilog
// -> InMemoryLogSink (whose text feeds the UI log card and the log file). Drives the real
// ScheduledTaskAutostartService through the invoker seam with synthetic localized output; no
// real scheduled task and no real Windows ACL is touched.
public class ScheduledTaskAutostartRenderTests : IDisposable
{
    private readonly InMemoryLogSink _sink;
    private readonly SerilogLoggerFactory _factory;
    private readonly Serilog.Core.Logger _serilogLogger;

    public ScheduledTaskAutostartRenderTests()
    {
        _sink = new InMemoryLogSink(Path.Combine(
            Path.GetTempPath(), "ProcessGuardian.Tests", "render-" + Guid.NewGuid().ToString("N") + ".log"));
        _serilogLogger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Sink(_sink)
            .CreateLogger();
        _factory = new SerilogLoggerFactory(_serilogLogger);
    }

    public void Dispose()
    {
        _factory.Dispose();
        _serilogLogger.Dispose();
        _sink.Dispose();
    }

    [Fact]
    public async Task RenderedLogText_HasNoQuotesAroundStringProperties()
    {
        // Localized not-found on both the /Delete and the follow-up query: the delete exits
        // nonzero (positively classified missing task) and the removal verifies Absent.
        ScheduledTaskAutostartService.SchTasksInvoker invoker = (arguments, _) =>
            System.Threading.Tasks.Task.FromResult(
                new ScheduledTaskAutostartService.SchTasksCommandResult(
                    1, string.Empty, "ОШИБКА: Не удается найти указанный файл.", null));
        var logger = _factory.CreateLogger<ScheduledTaskAutostartService>();
        // The disable path never stages; the production staging store stays unexercised here.
        var service = new ScheduledTaskAutostartService(logger, invoker);

        var result = await service.RemoveRegistrationAsync();

        result.Succeeded.Should().BeTrue("a verified absence is an idempotent success");

        var text = _sink.GetRecentLogs();

        // The routine missing-task state is silent (R-AS-AUTOSTART-QUERY-DEDUP): no Debug/Info
        // entry with an exit code and no raw localized stream text; the only entry is the single
        // verified-removal Information, logged after the post-verification.
        text.Should().NotContain("Autostart task is absent",
            "a routine missing-task pre-read state is never logged");
        text.Should().NotContain("(exit 1)",
            "no provisional exit-code message precedes the verification");
        text.Should().NotContain("delete reported nonzero");
        text.Should().NotContain("delete command reported success");
        text.Should().Contain("Autostart task removal verified; no registration remains",
            "exactly one concise Information entry follows the verified absence");
        text.Should().NotContain("ERROR: The system cannot find the file specified.");
        text.Should().NotContain("Не удается найти указанный файл",
            "raw localized stdout/stderr never enters routine logs");
        text.Should().NotContain("ERR");

        // Exercise the genuine-failure rendering (LogCommandFailure with :l scalars) and assert
        // no unintended quotes around the operation name or the diagnostic streams.
        _sink.Clear();
        var deniedService = new ScheduledTaskAutostartService(
            logger,
            (arguments, _) => System.Threading.Tasks.Task.FromResult(
                new ScheduledTaskAutostartService.SchTasksCommandResult(
                    1, "ОШИБКА: Отказано в доступе.", string.Empty, null)));

        var failed = await deniedService.RemoveRegistrationAsync();
        failed.Succeeded.Should().BeFalse(); // query Unknown (unrecognized output) -> fail closed
        var rendered = _sink.GetRecentLogs();
        rendered.Should().Contain("Autostart delete failed (exit 1)");
        rendered.Should().NotContain("\"delete\"", "the operation name renders as a literal, not a quoted scalar");
        rendered.Should().NotContain("\"ОШИБКА", "the diagnostic streams render as literal text, not quoted scalars");
        rendered.Should().Contain("ОШИБКА: Отказано в доступе.",
            "the bounded localized diagnostics stay visible in the log");
    }
}
