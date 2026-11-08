using System.Diagnostics;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ProcessGuardian.Domain.Enums;
using ProcessGuardian.Infrastructure.Autostart;
using Xunit;

namespace ProcessGuardian.Infrastructure.Tests;

// R-AS-TASK-SCHEDULER-DIAGNOSTICS regression tests: a positively classified missing task is the
// ordinary Absent state and is logged at Debug only (no raw localized stdout/stderr, no Error);
// a nonzero /Delete is provisional until the follow-up /Query /XML — a verified absence is an
// idempotent success with no false Error, while a still-present task or an unverifiable query
// fails closed with the bounded diagnostics logged at Error; AutostartResult.ErrorMessage stays
// stable English (localized schtasks output and raw exception messages never leak into the
// field MainViewModel displays as AutostartError). No test logs or asserts the full task XML.
// Runs through the SchTasksInvoker seam; no real scheduled task is touched.
public class ScheduledTaskAutostartDiagnosticsTests
{
    private static string ExpectedCommand() =>
        Process.GetCurrentProcess().MainModule?.FileName ?? "C:\\Unknown\\app.exe";

    private static ScheduledTaskAutostartService.SchTasksCommandResult Result(
        int exitCode, string stdout, string stderr, Exception? exception) =>
        new(exitCode, stdout, stderr, exception);

    // A clean task-not-found /Delete result with a SYNTHETIC localized not-found text: proves the
    // classifier still works (the not-found marker) and the localized text stays out of routine
    // logs and the user-facing error message.
    private static ScheduledTaskAutostartService.SchTasksCommandResult LocalizedNotFound() =>
        Result(1, string.Empty, "ОШИБКА: Не удается найти указанный файл.", null);

    private static ScheduledTaskAutostartService.SchTasksCommandResult NotFound() =>
        Result(1, string.Empty, "ERROR: The system cannot find the file specified.", null);

    private static ScheduledTaskAutostartService.SchTasksCommandResult AccessDenied() =>
        Result(1, string.Empty, "ERROR: Access is denied.", null);

    // Capturing logger with levels and exceptions for the exception-diagnostics assertions.
    private sealed class CapturingLogger : ILogger<ScheduledTaskAutostartService>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add((logLevel, formatter(state, exception), exception));
    }

    private static (ScheduledTaskAutostartService Service, CapturingLogger Logger) NewService(
        Func<string, ScheduledTaskAutostartService.SchTasksCommandResult> script,
        CapturingLogger? logger = null)
    {
        logger ??= new CapturingLogger();
        ScheduledTaskAutostartService.SchTasksInvoker invoker = (arguments, _) =>
            System.Threading.Tasks.Task.FromResult(script(arguments));
        return (new ScheduledTaskAutostartService(logger, invoker, new FakeStagingStore()), logger);
    }

    // Scripted sequence for the disable path: query #1 reports a present (Enabled) task, the
    // /Delete returns the configured result, and every follow-up query returns `postQuery`.
    private static (ScheduledTaskAutostartService Service, CapturingLogger Logger) DisableHarness(
        ScheduledTaskAutostartService.SchTasksCommandResult deleteResult,
        ScheduledTaskAutostartService.SchTasksCommandResult postQuery)
    {
        var queries = 0;
        return NewService(arguments =>
        {
            if (arguments.Contains("/Query", StringComparison.Ordinal))
            {
                queries++;
                return queries == 1
                    ? Result(0, ScheduledTaskTestXml.TaskXml(command: ExpectedCommand()), string.Empty, null)
                    : postQuery;
            }
            return deleteResult;
        });
    }

    // ------------------------------------------------------------------
    // MissingTask: Absent with routine Debug diagnostics only
    // ------------------------------------------------------------------

    [Fact]
    public async Task ReadState_PositiveMissingTask_Absent_LogsDebugOnly_NoRawLocalizedOutput()
    {
        var (service, logger) = NewService(arguments =>
            arguments.Contains("/Query", StringComparison.Ordinal)
                ? Result(1, string.Empty, "ОШИБКА: Не удается найти указанный файл.", null)
                : Result(0, string.Empty, string.Empty, null));

        var state = await service.ReadStateAsync();

        state.Should().Be(AutostartState.Absent,
            "a positively classified task-not-found response is the verified Absent state");
        logger.Messages.Should().Contain(m =>
            m.Level == LogLevel.Debug
            && m.Message.Contains("Autostart task is absent", StringComparison.Ordinal));
        logger.Messages.Should().NotContain(m => m.Level >= LogLevel.Information,
            "a missing task is an ordinary state: no Information/Error, only concise Debug");
        logger.Messages.Should().NotContain(m =>
            m.Message.Contains("Не удается найти", StringComparison.Ordinal),
            "raw localized schtasks output never enters routine logs");
        logger.Messages.Should().NotContain(m =>
            m.Message.Contains("ERROR", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Disable_FromAbsent_FastPath_LogsNothingWithRawOutput()
    {
        var (service, logger) = NewService(arguments =>
            arguments.Contains("/Query", StringComparison.Ordinal)
                ? LocalizedNotFound()
                : Result(0, string.Empty, string.Empty, null));

        var result = await service.SetEnabledAsync(false);

        result.Succeeded.Should().BeTrue();
        result.ActualState.Should().Be(AutostartState.Absent);
        logger.Messages.Should().OnlyContain(m => m.Level <= LogLevel.Debug);
        logger.Messages.Should().NotContain(m =>
            m.Message.Contains("Не удается найти", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------
    // RemoveRegistrationAsync: nonzero delete + verified absence → Success, no Error
    // ------------------------------------------------------------------

    [Fact]
    public async Task RemoveRegistration_DeleteNonZero_VerifiedAbsent_Success_NoErrorLogged()
    {
        var (service, logger) = NewService(arguments =>
        {
            if (arguments.Contains("/Delete", StringComparison.Ordinal))
                return LocalizedNotFound(); // nonzero delete: not-found (already removed)
            return LocalizedNotFound(); // follow-up query: positively classified absent
        });

        var result = await service.RemoveRegistrationAsync();

        result.Succeeded.Should().BeTrue(
            "a verified absence is the required postcondition; the nonzero delete was a benign not-found");
        result.ActualState.Should().Be(AutostartState.Absent);
        logger.Messages.Should().NotContain(m => m.Level == LogLevel.Error,
            "the preceding Error for the nonzero delete is false once the absence is verified");
        logger.Messages.Should().Contain(m =>
            m.Level == LogLevel.Debug
            && m.Message.Contains("delete reported nonzero", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------
    // SetEnabledAsync(false): concurrent removal between pre-query and delete
    // ------------------------------------------------------------------

    [Fact]
    public async Task Disable_ConcurrentRemoval_DeleteNonZero_VerifiedAbsent_IdempotentSuccess_NoError()
    {
        // The pre-query sees a present task; another actor removes it before the /Delete, which
        // then exits nonzero (not-found); the follow-up query verifies Absent — an idempotent
        // success, never a failure and never a false Error.
        var (service, logger) = DisableHarness(
            LocalizedNotFound(), LocalizedNotFound());

        var result = await service.SetEnabledAsync(false);

        result.Succeeded.Should().BeTrue("the desired absent state was already achieved");
        result.ActualState.Should().Be(AutostartState.Absent);
        logger.Messages.Should().NotContain(m => m.Level == LogLevel.Error);
        logger.Messages.Should().Contain(m =>
            m.Level == LogLevel.Debug
            && m.Message.Contains("delete reported nonzero", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------
    // Genuine failures: bounded diagnostics at Error, localized text in logs only
    // ------------------------------------------------------------------

    [Fact]
    public async Task RemoveRegistration_DeleteNonZero_QueryUnknown_FailsClosed_WithBoundedDiagnostics()
    {
        var (service, logger) = NewService(arguments =>
            arguments.Contains("/Delete", StringComparison.Ordinal)
                ? Result(1, "ОШИБКА: Отказано в доступе.", string.Empty, null) // localized delete output
                : AccessDenied());

        var result = await service.RemoveRegistrationAsync();

        result.Succeeded.Should().BeFalse();
        result.ActualState.Should().Be(AutostartState.Unknown);
        logger.Messages.Should().Contain(m =>
            m.Level == LogLevel.Error
            && m.Message.Contains("delete failed", StringComparison.Ordinal)
            && m.Message.Contains("Отказано в доступе", StringComparison.Ordinal),
            "the bounded localized delete diagnostics stay in the structured log");
        result.ErrorMessage.Should().NotContain("ОШИБКА", "localized command output never enters the UI-facing error");
        result.ErrorMessage.Should().Contain("could not be verified");
    }

    [Fact]
    public async Task Disable_DeleteNonZero_QueryUnknown_FailsClosed_WithBoundedDiagnostics()
    {
        // The pre-query reports a present task; the /Delete exits nonzero with localized output;
        // the follow-up query is Unknown (access denied) — fail closed, with the bounded delete
        // diagnostics re-logged at Error.
        var queries = 0;
        var logger = new CapturingLogger();
        ScheduledTaskAutostartService.SchTasksInvoker invoker = (arguments, _) =>
        {
            if (arguments.Contains("/Query", StringComparison.Ordinal))
            {
                queries++;
                return System.Threading.Tasks.Task.FromResult(queries == 1
                    ? Result(0, ScheduledTaskTestXml.TaskXml(command: ExpectedCommand()), string.Empty, null)
                    : AccessDenied());
            }
            return System.Threading.Tasks.Task.FromResult(
                Result(1, "ОШИБКА: Отказано в доступе.", string.Empty, null));
        };
        var service = new ScheduledTaskAutostartService(logger, invoker, new FakeStagingStore());

        var result = await service.SetEnabledAsync(false);

        result.Succeeded.Should().BeFalse();
        result.ActualState.Should().Be(AutostartState.Unknown);
        logger.Messages.Should().Contain(m =>
            m.Level == LogLevel.Error
            && m.Message.Contains("delete failed", StringComparison.Ordinal)
            && m.Message.Contains("Отказано в доступе", StringComparison.Ordinal),
            "the genuine failure re-logs the bounded delete diagnostics at Error");
        result.ErrorMessage.Should().NotContain("ОШИБКА",
            "localized command output never enters the UI-facing error");
    }

    [Fact]
    public async Task Disable_DeleteNonZero_StillPresent_GenuineFailure()
    {
        // The delete exits nonzero AND the follow-up query still finds the task registered: a
        // genuine failure with the bounded delete diagnostics at Error.
        var (service, logger) = DisableHarness(
            Result(1, "ОШИБКА: Параметр задан неверно.", string.Empty, null),
            Result(0, ScheduledTaskTestXml.TaskXml(command: ExpectedCommand()), string.Empty, null));

        var result = await service.SetEnabledAsync(false);

        result.Succeeded.Should().BeFalse();
        result.ActualState.Should().Be(AutostartState.Enabled);
        result.ErrorMessage.Should().Contain("still registered after the delete attempt");
        result.ErrorMessage.Should().NotContain("Параметр", "localized output never enters the UI-facing error");
        logger.Messages.Should().Contain(m =>
            m.Level == LogLevel.Error
            && m.Message.Contains("delete failed", StringComparison.Ordinal)
            && m.Message.Contains("Параметр задан неверно", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Disable_DeleteExitZero_StillPresent_GenuineFailure()
    {
        // The delete exits 0 but the task is still registered: still a failure (the exit code is
        // never proof).
        var (service, logger) = DisableHarness(
            Result(0, "SUCCESS", string.Empty, null),
            Result(0, ScheduledTaskTestXml.TaskXml(command: ExpectedCommand()), string.Empty, null));

        var result = await service.SetEnabledAsync(false);

        result.Succeeded.Should().BeFalse();
        result.ActualState.Should().Be(AutostartState.Enabled);
        result.ErrorMessage.Should().Contain("still registered after the delete attempt");
    }

    [Fact]
    public async Task RemoveRegistration_DeleteExitZero_StillPresent_GenuineFailure()
    {
        // Covered by the existing ErrorTests suite (DeleteZero_QueryStillPresent); this variant
        // pins the no-XML and English-only invariants for the same path.
        var (service, logger) = DisableHarness(
            Result(0, "SUCCESS", string.Empty, null),
            Result(0, ScheduledTaskTestXml.TaskXml(command: ExpectedCommand()), string.Empty, null));

        var result = await service.RemoveRegistrationAsync();

        result.Succeeded.Should().BeFalse();
        result.ActualState.Should().Be(AutostartState.Enabled);
        result.ErrorMessage.Should().Contain("still present", "the recovery-primitive wording is unchanged");
        logger.Messages.Should().NotContain(m =>
            m.Message.Contains("<Task", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------
    // Create path: localized output and exception messages stay in logs only
    // ------------------------------------------------------------------

    [Fact]
    public async Task Enable_CreateNonZeroExit_LocalizedOutput_InLogsOnly_EnglishErrorMessage()
    {
        var (service, logger) = NewService(arguments =>
        {
            if (arguments.Contains("/Query", StringComparison.Ordinal))
                return NotFound(); // pre-state read: absent -> create runs
            return Result(2, "ОШИБКА: Не удалось создать задачу.", string.Empty, null);
        });

        var result = await service.SetEnabledAsync(true);

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Be("task creation failed (exit 2)");
        result.ErrorMessage.Should().NotContain("ОШИБКА",
            "localized schtasks output never enters the UI-facing error");
        logger.Messages.Should().Contain(m =>
            m.Level == LogLevel.Error
            && m.Message.Contains("create failed", StringComparison.Ordinal)
            && m.Message.Contains("Не удалось создать задачу", StringComparison.Ordinal),
            "the localized output stays in the bounded structured log");
    }

    [Fact]
    public async Task Enable_CreateInvocationException_LocalizedMessage_InLogOnly_EnglishErrorMessage()
    {
        var queries = 0;
        ScheduledTaskAutostartService.SchTasksInvoker invoker = (arguments, _) =>
        {
            if (arguments.Contains("/Query", StringComparison.Ordinal))
            {
                queries++;
                return System.Threading.Tasks.Task.FromResult(NotFound());
            }
            throw new InvalidOperationException("Невозможно запустить процесс.");
        };
        var logger = new CapturingLogger();
        var service = new ScheduledTaskAutostartService(
            logger, invoker, new FakeStagingStore());

        var result = await service.SetEnabledAsync(true);

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Be("task creation failed: schtasks.exe could not be started");
        result.ErrorMessage.Should().NotContain("Невозможно", "a localized exception message never enters the UI-facing error");

        // The full exception information stays in the structured log: an Error entry carrying the
        // exception object itself (its localized Message renders into the sink's exception field).
        var logged = logger.Messages.Should().Contain(m =>
                m.Level == LogLevel.Error
                && m.Message.Contains("create failed", StringComparison.Ordinal)
                && m.Exception != null)
            .Subject;
        logged.Exception!.Message.Should().Contain("Невозможно запустить процесс",
            "the detailed exception stays in the structured log, not in the UI-facing error");
        queries.Should().Be(1, "an invocation failure short-circuits before the verification query");
    }

    // ------------------------------------------------------------------
    // Bound(): display-only normalization — trim before cap
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("\r\n  abc  \r\n", "abc")]
    [InlineData("abc\r\n", "abc")]
    [InlineData("\t abc \t", "abc")]
    public void Bound_TrimsSurroundingWhitespaceAndLineBreaks(string input, string expected)
    {
        ScheduledTaskAutostartService.SchTasksCommandResult.Bound(input).Should().Be(expected);
    }

    [Fact]
    public void Bound_PreservesInternalWhitespace()
    {
        ScheduledTaskAutostartService.SchTasksCommandResult.Bound("line1\r\nline2\r\nline3")
            .Should().Be("line1\r\nline2\r\nline3", "internal text and breaks are preserved; only the ends are trimmed");
    }

    [Fact]
    public void Bound_CapIsAppliedAfterTrimming()
    {
        // 5000 'x' + trailing CRLF: trimming first yields 5000 x's, then the 4096 cap applies.
        var input = new string('x', 5000) + "\r\n";
        var bounded = ScheduledTaskAutostartService.SchTasksCommandResult.Bound(input);

        bounded.Should().StartWith(new string('x', 4096));
        bounded.Should().Contain("[truncated at 4096]");
        bounded.Should().EndWith("[truncated at 4096]", "the trailing break was trimmed before capping");
    }

    [Fact]
    public void Bound_SmallInputWithSurroundingBreaks_IsFullyTrimmed()
    {
        var input = "\r\nline\r\n";
        var bounded = ScheduledTaskAutostartService.SchTasksCommandResult.Bound(input);

        bounded.Should().Be("line");
        bounded.Should().NotContain("\r").And.NotContain("\n");
    }

    [Fact]
    public void Bound_NullAndEmpty_RemainEmpty()
    {
        ScheduledTaskAutostartService.SchTasksCommandResult.Bound(null).Should().BeEmpty();
        ScheduledTaskAutostartService.SchTasksCommandResult.Bound(string.Empty).Should().BeEmpty();
        ScheduledTaskAutostartService.SchTasksCommandResult.Bound("\r\n  \r\n").Should().BeEmpty();
    }

    // ------------------------------------------------------------------
    // Invariants: no full task XML anywhere in logs
    // ------------------------------------------------------------------

    [Fact]
    public async Task Diagnostics_NeverLogTheFullTaskXml()
    {
        var xml = ScheduledTaskAutostartService.BuildTaskDefinitionXml(ExpectedCommand());
        var (service, logger) = DisableHarness(
            LocalizedNotFound(), LocalizedNotFound());

        await service.SetEnabledAsync(false);
        await service.RemoveRegistrationAsync();

        logger.Messages.Should().NotBeEmpty();
        logger.Messages.Should().NotContain(m => m.Message.Contains("<Task", StringComparison.Ordinal));
        logger.Messages.Should().NotContain(m => m.Message.Contains("RunLevel", StringComparison.Ordinal));
        logger.Messages.Should().NotContain(m => m.Message.Contains(xml, StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------
    // Regex sanity: localized not-found markers stay parseable (classifier unchanged)
    // ------------------------------------------------------------------

    [Fact]
    public async Task LocalizedNotFoundMarkers_ClassifierUnchanged_PositiveAbsent()
    {
        var (service, _) = NewService(arguments =>
            arguments.Contains("/Query", StringComparison.Ordinal)
                ? LocalizedNotFound()
                : Result(0, string.Empty, string.Empty, null));

        var state = await service.ReadStateAsync();

        state.Should().Be(AutostartState.Absent,
            "the classifier still accepts the localized not-found response; only the logging changed");
    }
}
