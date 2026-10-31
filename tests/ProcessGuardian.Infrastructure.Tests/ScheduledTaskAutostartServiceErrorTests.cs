using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ProcessGuardian.Infrastructure.Autostart;
using Xunit;

namespace ProcessGuardian.Infrastructure.Tests;

// Autostart reliability (admin edition): the scheduled-task service must surface a
// typed, verified outcome. A nonzero schtasks.exe exit code, a process-start failure,
// or a command that exits 0 without the task actually existing are all failures — never
// silently lost. Runs through the SchTasksInvoker seam; no real scheduled task is touched.
public class ScheduledTaskAutostartServiceErrorTests
{
    // A scriptable schtasks.exe result keyed by the argument verb, returned as the
    // (ExitCode, Output) tuple the service consumes. The first /Query (the current-state
    // read) returns QueryExitCode; a later /Query (the post-operation verification) returns
    // VerifyExitCode so we can simulate a command that exits 0 without the state taking effect.
    private sealed class Script
    {
        private int _queryCalls;

        public required int QueryExitCode { get; init; }
        public required int VerifyExitCode { get; init; }
        public required int MutatingExitCode { get; init; }
        public Exception? ThrowOnMutate { get; init; }

        public (int ExitCode, string Output) For(string arguments)
        {
            if (arguments.Contains("/Query", System.StringComparison.Ordinal))
            {
                _queryCalls++;
                var code = _queryCalls == 1 ? QueryExitCode : VerifyExitCode;
                return (code, code == 0 ? "SUCCESS: The task is available." : "ERROR: The system cannot find the task.");
            }

            if (ThrowOnMutate is { } ex)
                throw ex;
            return (MutatingExitCode, MutatingExitCode == 0 ? "SUCCESS: The task has been created." : "ERROR: The parameter is incorrect.");
        }
    }

    private static ScheduledTaskAutostartService NewService(Script script)
    {
        ScheduledTaskAutostartService.SchTasksInvoker invoker = (arguments, _) =>
            System.Threading.Tasks.Task.FromResult(script.For(arguments));
        return new ScheduledTaskAutostartService(NullLogger<ScheduledTaskAutostartService>.Instance, invoker);
    }

    [Fact]
    public async Task Enable_SuccessfulCreate_ReportsSucceededWithVerifiedState()
    {
        // Query (disabled), create succeeds, verify query shows the task present.
        var service = NewService(new Script { QueryExitCode = 1, MutatingExitCode = 0, VerifyExitCode = 0 });

        var result = await service.SetEnabledAsync(true);

        result.Succeeded.Should().BeTrue();
        result.ActualState.Should().BeTrue();
    }

    [Fact]
    public async Task Enable_NonZeroSchTasksExitCode_ReportsFailedWithDiagnostic()
    {
        // Query disabled, create fails with a nonzero schtasks exit code.
        var service = NewService(new Script { QueryExitCode = 1, MutatingExitCode = 1, VerifyExitCode = 1 });

        var result = await service.SetEnabledAsync(true);

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
        result.ErrorMessage.Should().Contain("exit 1");
    }

    [Fact]
    public async Task Enable_ProcessStartException_ReportsFailedNotThrown()
    {
        var service = NewService(new Script
        {
            QueryExitCode = 1,
            MutatingExitCode = 0,
            VerifyExitCode = 0,
            ThrowOnMutate = new InvalidOperationException("boom"),
        });

        var result = await service.SetEnabledAsync(true);

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("boom");
    }

    [Fact]
    public async Task Enable_CommandSucceedsButTaskNotRegisterd_ReportsFailed()
    {
        // Query disabled, create reports success (exit 0), but the verification query still
        // reports the task absent: an exit code alone must not be trusted.
        var service = NewService(new Script { QueryExitCode = 1, MutatingExitCode = 0, VerifyExitCode = 1 });

        var result = await service.SetEnabledAsync(true);

        result.Succeeded.Should().BeFalse();
        result.ActualState.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Disable_NonZeroSchTasksExitCode_ReportsFailed()
    {
        // Query (present), delete fails with a nonzero schtasks exit code.
        var service = NewService(new Script { QueryExitCode = 0, MutatingExitCode = 1, VerifyExitCode = 0 });

        var result = await service.SetEnabledAsync(false);

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("exit 1");
    }

    [Fact]
    public async Task Enable_WhenAlreadyEnabled_IsIdempotentSucceedsWithoutMutating()
    {
        // Already enabled (query exit 0); the fast path must not run a mutating command.
        var invokedMutate = false;
        ScheduledTaskAutostartService.SchTasksInvoker invoker = (arguments, _) =>
        {
            if (!arguments.Contains("/Query", System.StringComparison.Ordinal))
                invokedMutate = true;
            return System.Threading.Tasks.Task.FromResult(arguments.Contains("/Query", System.StringComparison.Ordinal)
                ? (0, "SUCCESS")
                : (0, "SUCCESS"));
        };
        var service = new ScheduledTaskAutostartService(NullLogger<ScheduledTaskAutostartService>.Instance, invoker);

        var result = await service.SetEnabledAsync(true);

        result.Succeeded.Should().BeTrue();
        result.ActualState.Should().BeTrue();
        invokedMutate.Should().BeFalse("idempotent fast path must not re-run the create when already enabled");
    }
}
