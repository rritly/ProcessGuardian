using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ProcessGuardian.Domain.Enums;
using ProcessGuardian.Infrastructure.Autostart;
using Xunit;

namespace ProcessGuardian.Infrastructure.Tests;

// Typed autostart state (admin edition): the scheduled-task service must surface a verified
// AutostartState read (Enabled/Disabled/Misconfigured/Absent/Unknown) from the machine-readable
// task XML, never from localized text or a raw exit code. A read/query/access failure is Unknown,
// never Absent; a nonzero /Query exit code is Absent only for a clean "task not found". Runs
// through the SchTasksInvoker seam; no real scheduled task is touched.
public class ScheduledTaskAutostartServiceErrorTests
{
    // A valid task export with the given parameter overrides, so /Query /XML results can be
    // scripted as Enabled/Disabled/Misconfigured. Defaults match the expected autostart task.
    private static string TaskXml(
        string? command = null,
        string? arguments = " --background ",
        bool logonTrigger = true,
        string? logonType = "InteractiveToken",
        string? runLevel = "HighestAvailable",
        string? enabled = "true")
    {
        var trigger = logonTrigger ? "<LogonTrigger />" : "<BootTrigger />";
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Triggers>
                {trigger}
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <LogonType>{logonType}</LogonType>
                  <RunLevel>{runLevel}</RunLevel>
                </Principal>
              </Principals>
              <Actions>
                <Exec>
                  <Command>{command}</Command>
                  <Arguments>{arguments}</Arguments>
                </Exec>
              </Actions>
              <Settings>
                <Enabled>{enabled}</Enabled>
              </Settings>
            </Task>
            """;
    }

    private static string ExpectedCommand() =>
        Process.GetCurrentProcess().MainModule?.FileName ?? "C:\\Unknown\\app.exe";

    // A scriptable schtasks.exe result keyed by the verb. /Query /XML reads return TaskXml
    // (when the exit code is 0) or the given error output; create/delete return MutatingExitCode.
    private sealed class Script
    {
        public required int QueryExitCode { get; init; }
        public string QueryXml { get; init; } = string.Empty;
        public string QueryError { get; init; } = "ERROR: Access is denied.";
        public required int MutatingExitCode { get; init; }
        public Exception? ThrowOnMutate { get; init; }

        public ScheduledTaskAutostartService.SchTasksCommandResult For(string arguments)
        {
            if (arguments.Contains("/Query", StringComparison.Ordinal))
                return QueryExitCode == 0
                    ? new ScheduledTaskAutostartService.SchTasksCommandResult(0, QueryXml, string.Empty, null)
                    : new ScheduledTaskAutostartService.SchTasksCommandResult(QueryExitCode, string.Empty, QueryError, null);

            if (ThrowOnMutate is { } ex)
                return new ScheduledTaskAutostartService.SchTasksCommandResult(-1, string.Empty, string.Empty, ex);
            return MutatingExitCode == 0
                ? new ScheduledTaskAutostartService.SchTasksCommandResult(0, "SUCCESS", string.Empty, null)
                : new ScheduledTaskAutostartService.SchTasksCommandResult(MutatingExitCode, string.Empty, "ERROR: The parameter is incorrect.", null);
        }
    }

    private static ScheduledTaskAutostartService NewService(Script script)
    {
        ScheduledTaskAutostartService.SchTasksInvoker invoker = (arguments, _) =>
            System.Threading.Tasks.Task.FromResult(script.For(arguments));
        return new ScheduledTaskAutostartService(NullLogger<ScheduledTaskAutostartService>.Instance, invoker);
    }

    // ---------------------------------------------------------------------
    // ReadStateAsync: typed classification from the task XML
    // ---------------------------------------------------------------------

    [Fact]
    public async Task ReadState_EnabledTaskXml_IsEnabled()
    {
        var service = NewService(new Script
        {
            QueryExitCode = 0,
            QueryXml = TaskXml(command: ExpectedCommand()),
            MutatingExitCode = 0,
        });

        var state = await service.ReadStateAsync();

        state.Should().Be(AutostartState.Enabled);
    }

    [Fact]
    public async Task ReadState_DisabledFlagXml_IsDisabled_NotEnabled()
    {
        var service = NewService(new Script
        {
            QueryExitCode = 0,
            QueryXml = TaskXml(command: ExpectedCommand(), enabled: "false"),
            MutatingExitCode = 0,
        });

        var state = await service.ReadStateAsync();

        state.Should().Be(AutostartState.Disabled);
    }

    [Fact]
    public async Task ReadState_MissingCommand_IsMisconfigured()
    {
        var xml = TaskXml(command: null);
        var service = NewService(new Script { QueryExitCode = 0, QueryXml = xml, MutatingExitCode = 0 });

        var state = await service.ReadStateAsync();

        state.Should().Be(AutostartState.Misconfigured);
    }

    [Fact]
    public async Task ReadState_WrongCommand_IsMisconfigured()
    {
        var xml = TaskXml(command: @"C:\Other\app.exe");
        var service = NewService(new Script { QueryExitCode = 0, QueryXml = xml, MutatingExitCode = 0 });

        var state = await service.ReadStateAsync();

        state.Should().Be(AutostartState.Misconfigured);
    }

    [Fact]
    public async Task ReadState_MissingBackgroundFlag_IsMisconfigured()
    {
        var xml = TaskXml(command: ExpectedCommand(), arguments: null);
        var service = NewService(new Script { QueryExitCode = 0, QueryXml = xml, MutatingExitCode = 0 });

        var state = await service.ReadStateAsync();

        state.Should().Be(AutostartState.Misconfigured);
    }

    [Fact]
    public async Task ReadState_WrongBackgroundFlag_IsMisconfigured()
    {
        var xml = TaskXml(command: ExpectedCommand(), arguments: " --no-background ");
        var service = NewService(new Script { QueryExitCode = 0, QueryXml = xml, MutatingExitCode = 0 });

        var state = await service.ReadStateAsync();

        state.Should().Be(AutostartState.Misconfigured);
    }

    [Fact]
    public async Task ReadState_NonInteractiveLogon_IsMisconfigured()
    {
        var service = NewService(new Script
        {
            QueryExitCode = 0,
            QueryXml = TaskXml(command: ExpectedCommand(), logonType: "Password"),
            MutatingExitCode = 0,
        });

        var state = await service.ReadStateAsync();

        state.Should().Be(AutostartState.Misconfigured);
    }

    [Fact]
    public async Task ReadState_WrongRunLevel_IsMisconfigured()
    {
        var service = NewService(new Script
        {
            QueryExitCode = 0,
            QueryXml = TaskXml(command: ExpectedCommand(), runLevel: "LeastPrivilege"),
            MutatingExitCode = 0,
        });

        var state = await service.ReadStateAsync();

        state.Should().Be(AutostartState.Misconfigured);
    }

    [Fact]
    public async Task ReadState_NonLogonTrigger_IsMisconfigured()
    {
        var service = NewService(new Script
        {
            QueryExitCode = 0,
            QueryXml = TaskXml(command: ExpectedCommand(), logonTrigger: false),
            MutatingExitCode = 0,
        });

        var state = await service.ReadStateAsync();

        state.Should().Be(AutostartState.Misconfigured);
    }

    [Fact]
    public async Task ReadState_MissingEnabledElement_DefaultsToEnabled()
    {
        // [R-AS-XML-VERIFY] The Task Scheduler schema default for the optional <Settings/Enabled>
        // element is true when <Settings> is present: a verified task without an <Enabled> child
        // reads as Enabled, not Unknown.
        var xml = TaskXml(command: ExpectedCommand(), enabled: "true")
            .Replace("<Enabled>true</Enabled>", string.Empty, StringComparison.Ordinal);
        var service = NewService(new Script { QueryExitCode = 0, QueryXml = xml, MutatingExitCode = 0 });

        var state = await service.ReadStateAsync();

        state.Should().Be(AutostartState.Enabled);
    }

    [Fact]
    public async Task ReadState_MissingSettingsElement_IsUnknown_NotAutoEnabled()
    {
        // The schema default applies only when <Settings> itself exists; a task without the
        // whole <Settings> block stays fail-closed Unknown through the typed read.
        var xml = System.Text.RegularExpressions.Regex.Replace(
            TaskXml(command: ExpectedCommand()),
            @"\s*<Settings>.*?</Settings>\s*",
            string.Empty,
            System.Text.RegularExpressions.RegexOptions.Singleline);
        var service = NewService(new Script { QueryExitCode = 0, QueryXml = xml, MutatingExitCode = 0 });

        var state = await service.ReadStateAsync();

        state.Should().Be(AutostartState.Unknown);
    }

    [Fact]
    public async Task ReadState_UnparsableQueryXml_IsUnknown()
    {
        var service = NewService(new Script
        {
            QueryExitCode = 0,
            QueryXml = "NOT VALID XML",
            MutatingExitCode = 0,
        });

        var state = await service.ReadStateAsync();

        state.Should().Be(AutostartState.Unknown);
    }

    [Fact]
    public async Task ReadState_QueryNonZeroWithNotFoundMarker_IsAbsent()
    {
        var service = NewService(new Script
        {
            QueryExitCode = 1,
            QueryError = "ERROR: The system cannot find the file specified.",
            MutatingExitCode = 0,
        });

        var state = await service.ReadStateAsync();

        state.Should().Be(AutostartState.Absent);
    }

    [Fact]
    public async Task ReadState_QueryNonZeroAccessDenied_IsUnknown_NeverAbsent()
    {
        var service = NewService(new Script
        {
            QueryExitCode = 1,
            QueryError = "ERROR: Access is denied.",
            MutatingExitCode = 0,
        });

        var state = await service.ReadStateAsync();

        // A read/access failure must never be reported as a verified "off" registration.
        state.Should().Be(AutostartState.Unknown);
    }

    // ---------------------------------------------------------------------
    // Diagnosable failure classification (R-AS-DIAG): separate stdout/stderr, exception capture,
    // bounded logging, and distinct fail-closed causes. The typed state remains Unknown for every
    // cause except a clean verified task-not-found; diagnostics never weaken fail-closed behavior.
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("", "ERROR: The system cannot find the file specified.")]
    [InlineData("ERROR: The system cannot find the file specified.", "")]
    [InlineData("ERROR: System cannot find the file specified.", "")]
    public async Task ReadState_NonZeroQueryNotFoundMarkerOnEitherStream_IsAbsent(string stdout, string stderr)
    {
        // stdout and stderr are kept separate in the result; the "not found" marker may arrive on
        // either stream and must still classify as Absent — never as Unknown just because it did
        // not land on the stream a caller happened to inspect.
        ScheduledTaskAutostartService.SchTasksInvoker invoker = (_, _) =>
            System.Threading.Tasks.Task.FromResult(
                new ScheduledTaskAutostartService.SchTasksCommandResult(1, stdout, stderr, null));
        var service = new ScheduledTaskAutostartService(NullLogger<ScheduledTaskAutostartService>.Instance, invoker);

        var state = await service.ReadStateAsync();

        state.Should().Be(AutostartState.Absent);
    }

    [Fact]
    public async Task ReadState_NonZeroQueryAccessDeniedOnStderr_IsUnknown_AndClassifiedAccessDenied()
    {
        var result = new ScheduledTaskAutostartService.SchTasksCommandResult(
            1, string.Empty, "ERROR: Access is denied.", null);

        ScheduledTaskAutostartService.ClassifyQueryResult(result)
            .Should().Be(ScheduledTaskAutostartService.SchTasksQueryFailure.AccessDenied);

        ScheduledTaskAutostartService.SchTasksInvoker invoker = (_, _) =>
            System.Threading.Tasks.Task.FromResult(result);
        var service = new ScheduledTaskAutostartService(NullLogger<ScheduledTaskAutostartService>.Instance, invoker);

        (await service.ReadStateAsync()).Should().Be(AutostartState.Unknown,
            "an access-denied read must stay Unknown, never Absent");
    }

    [Fact]
    public async Task ReadState_NonZeroQueryServiceUnavailable_IsUnknown_AndClassifiedServiceUnavailable()
    {
        var result = new ScheduledTaskAutostartService.SchTasksCommandResult(
            2, string.Empty, "ERROR: The service has not been started.", null);

        ScheduledTaskAutostartService.ClassifyQueryResult(result)
            .Should().Be(ScheduledTaskAutostartService.SchTasksQueryFailure.ServiceUnavailable);

        ScheduledTaskAutostartService.SchTasksInvoker invoker = (_, _) =>
            System.Threading.Tasks.Task.FromResult(result);
        var service = new ScheduledTaskAutostartService(NullLogger<ScheduledTaskAutostartService>.Instance, invoker);

        (await service.ReadStateAsync()).Should().Be(AutostartState.Unknown);
    }

    [Fact]
    public async Task ReadState_QueryInvocationException_IsUnknown_AndClassifiedInvocationFailure()
    {
        var result = new ScheduledTaskAutostartService.SchTasksCommandResult(
            -1, string.Empty, string.Empty, new InvalidOperationException("schtasks.exe could not be started"));

        ScheduledTaskAutostartService.ClassifyQueryResult(result)
            .Should().Be(ScheduledTaskAutostartService.SchTasksQueryFailure.InvocationFailure);

        ScheduledTaskAutostartService.SchTasksInvoker invoker = (_, _) =>
            System.Threading.Tasks.Task.FromResult(result);
        var service = new ScheduledTaskAutostartService(NullLogger<ScheduledTaskAutostartService>.Instance, invoker);

        (await service.ReadStateAsync()).Should().Be(AutostartState.Unknown,
            "an invocation failure must stay Unknown, never Absent");
    }

    [Fact]
    public async Task ReadState_NonZeroQueryLocalizedNotFound_IsAbsent()
    {
        // Concrete, tested localized not-found markers must classify as Absent; anything else
        // stays Unknown (fail-closed) and is never guessed.
        var result = new ScheduledTaskAutostartService.SchTasksCommandResult(
            1, string.Empty, "ОШИБКА: Не удается найти указанный файл.", null);

        ScheduledTaskAutostartService.ClassifyQueryResult(result)
            .Should().Be(ScheduledTaskAutostartService.SchTasksQueryFailure.MissingTask);
    }

    [Fact]
    public async Task ReadState_NonZeroQueryUnknownText_IsUnknown_AndClassifiedUnknown()
    {
        var result = new ScheduledTaskAutostartService.SchTasksCommandResult(
            1, string.Empty, "ERROR: Something completely unexpected happened.", null);

        ScheduledTaskAutostartService.ClassifyQueryResult(result)
            .Should().Be(ScheduledTaskAutostartService.SchTasksQueryFailure.Unknown);

        ScheduledTaskAutostartService.SchTasksInvoker invoker = (_, _) =>
            System.Threading.Tasks.Task.FromResult(result);
        var service = new ScheduledTaskAutostartService(NullLogger<ScheduledTaskAutostartService>.Instance, invoker);

        (await service.ReadStateAsync()).Should().Be(AutostartState.Unknown);
    }

    [Fact]
    public void CommandResult_BoundedText_CapsEachStreamIndependently()
    {
        var big = new string('x', 6000);
        var result = new ScheduledTaskAutostartService.SchTasksCommandResult(1, big, "ERR", null);

        var bounded = result.BoundedText;

        bounded.Should().NotContain(new string('x', 6000), "streams must be bounded before logging");
        bounded.Should().Contain("[truncated");
        bounded.Should().Contain("ERR", "the small stream is preserved, not swallowed by the large one");
        bounded.Length.Should().BeLessThan(6000);
        ScheduledTaskAutostartService.SchTasksCommandResult.Bound(null).Should().BeEmpty();
    }

    // ---------------------------------------------------------------------
    // SetEnabledAsync: verified outcomes with the typed state
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Enable_UnknownInitialState_ReportsFailed_NoMutation()
    {
        var mutateCalled = false;
        ScheduledTaskAutostartService.SchTasksInvoker invoker = (arguments, _) =>
        {
            if (!arguments.Contains("/Query", StringComparison.Ordinal))
                mutateCalled = true;
            return System.Threading.Tasks.Task.FromResult(
                new ScheduledTaskAutostartService.SchTasksCommandResult(1, string.Empty, "ERROR: Access is denied.", null));
        };
        var service = new ScheduledTaskAutostartService(NullLogger<ScheduledTaskAutostartService>.Instance, invoker);

        var result = await service.SetEnabledAsync(true);

        result.Succeeded.Should().BeFalse();
        result.ActualState.Should().Be(AutostartState.Unknown);
        mutateCalled.Should().BeFalse("an Unknown state must never trigger a mutating operation");
    }

    [Fact]
    public async Task Enable_FromAbsent_CreateSucceedsAndVerifiedEnabled_ReportsSucceeded()
    {
        var service = NewService(new Script
        {
            QueryExitCode = 1,
            QueryError = "ERROR: The system cannot find the file specified.",
            MutatingExitCode = 0,
        });
        // The post-operation verification query must return an Enabled XML: extend the script.
        var verifyCalled = false;
        ScheduledTaskAutostartService.SchTasksInvoker invoker = (arguments, _) =>
        {
            if (arguments.Contains("/Query", StringComparison.Ordinal))
            {
                if (verifyCalled)
                    return System.Threading.Tasks.Task.FromResult(
                        new ScheduledTaskAutostartService.SchTasksCommandResult(0, TaskXml(command: ExpectedCommand()), string.Empty, null));
                verifyCalled = true;
                return System.Threading.Tasks.Task.FromResult(
                    new ScheduledTaskAutostartService.SchTasksCommandResult(1, string.Empty, "ERROR: The system cannot find the file specified.", null));
            }
            return System.Threading.Tasks.Task.FromResult(
                new ScheduledTaskAutostartService.SchTasksCommandResult(0, "SUCCESS", string.Empty, null));
        };
        var svc = new ScheduledTaskAutostartService(NullLogger<ScheduledTaskAutostartService>.Instance, invoker);

        var result = await svc.SetEnabledAsync(true);

        result.Succeeded.Should().BeTrue();
        result.ActualState.Should().Be(AutostartState.Enabled);
    }

    [Fact]
    public async Task Enable_CreateNonZeroExitCode_ReportsFailedWithDiagnostic()
    {
        var service = NewService(new Script
        {
            QueryExitCode = 1,
            QueryError = "ERROR: The system cannot find the file specified.",
            MutatingExitCode = 1,
        });

        var result = await service.SetEnabledAsync(true);

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
        result.ErrorMessage.Should().Contain("exit 1");
    }

    [Fact]
    public async Task Enable_CreateSucceedsButTaskNotVerified_ReportsFailed()
    {
        // Absent -> create exits 0, but the verification query still reports the task absent.
        var verifyCalled = false;
        ScheduledTaskAutostartService.SchTasksInvoker invoker = (arguments, _) =>
        {
            if (arguments.Contains("/Query", StringComparison.Ordinal))
            {
                if (verifyCalled)
                    return System.Threading.Tasks.Task.FromResult(
                        new ScheduledTaskAutostartService.SchTasksCommandResult(1, string.Empty, "ERROR: The system cannot find the file specified.", null));
                verifyCalled = true;
                return System.Threading.Tasks.Task.FromResult(
                    new ScheduledTaskAutostartService.SchTasksCommandResult(1, string.Empty, "ERROR: The system cannot find the file specified.", null));
            }
            return System.Threading.Tasks.Task.FromResult(
                new ScheduledTaskAutostartService.SchTasksCommandResult(0, "SUCCESS", string.Empty, null));
        };
        var service = new ScheduledTaskAutostartService(NullLogger<ScheduledTaskAutostartService>.Instance, invoker);

        var result = await service.SetEnabledAsync(true);

        result.Succeeded.Should().BeFalse();
        result.ActualState.Should().Be(AutostartState.Absent);
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Disable_DeleteNonZeroExitCode_ReportsFailed()
    {
        // Enabled -> delete fails with a nonzero schtasks exit code.
        var service = NewService(new Script
        {
            QueryExitCode = 0,
            QueryXml = TaskXml(command: ExpectedCommand()),
            MutatingExitCode = 1,
        });

        var result = await service.SetEnabledAsync(false);

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("exit 1");
    }

    [Fact]
    public async Task Disable_FromAbsent_IsIdempotentSucceedsWithoutMutating()
    {
        var mutateCalled = false;
        ScheduledTaskAutostartService.SchTasksInvoker invoker = (arguments, _) =>
        {
            if (!arguments.Contains("/Query", StringComparison.Ordinal))
                mutateCalled = true;
            return System.Threading.Tasks.Task.FromResult(
                new ScheduledTaskAutostartService.SchTasksCommandResult(1, string.Empty, "ERROR: The system cannot find the file specified.", null));
        };
        var service = new ScheduledTaskAutostartService(NullLogger<ScheduledTaskAutostartService>.Instance, invoker);

        var result = await service.SetEnabledAsync(false);

        result.Succeeded.Should().BeTrue();
        result.ActualState.Should().Be(AutostartState.Absent);
        mutateCalled.Should().BeFalse("an already-absent registration must not re-run the delete");
    }

    [Fact]
    public async Task Enable_ProcessStartException_ReportsFailedNotThrown()
    {
        var service = NewService(new Script
        {
            QueryExitCode = 1,
            QueryError = "ERROR: The system cannot find the file specified.",
            MutatingExitCode = 0,
            ThrowOnMutate = new InvalidOperationException("boom"),
        });

        var result = await service.SetEnabledAsync(true);

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("boom");
    }

    // ---------------------------------------------------------------------
    // RemoveRegistrationAsync (recovery primitive, R-AS-AUTO): unconditional
    // /Delete followed by a positive Absent verification. The /Delete exit code alone is
    // never proof either way — only a follow-up read that confirms Absent reports success.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task RemoveRegistration_TaskAbsent_DeleteNonZero_VerifiedAbsent_ReportsSucceeded()
    {
        // The task is already gone (a previous, interrupted recovery): the /Delete exits nonzero
        // (not-found) but the positive follow-up read confirms Absent, so removal is verified.
        var service = NewService(new Script
        {
            QueryExitCode = 1,
            QueryError = "ERROR: The system cannot find the file specified.",
            MutatingExitCode = 1,
        });

        var result = await service.RemoveRegistrationAsync();

        result.Succeeded.Should().BeTrue();
        result.ActualState.Should().Be(AutostartState.Absent);
    }

    [Fact]
    public async Task RemoveRegistration_DeleteZero_QueryAbsent_ReportsSucceeded()
    {
        var service = NewService(new Script
        {
            QueryExitCode = 1,
            QueryError = "ERROR: The system cannot find the file specified.",
            MutatingExitCode = 0,
        });

        var result = await service.RemoveRegistrationAsync();

        result.Succeeded.Should().BeTrue();
        result.ActualState.Should().Be(AutostartState.Absent);
    }

    [Fact]
    public async Task RemoveRegistration_DeleteZero_QueryStillPresent_ReportsFailed()
    {
        // The /Delete said success but the task is still registered: the positive verification
        // detects it and fails closed so the recovery stays pending.
        var service = NewService(new Script
        {
            QueryExitCode = 0,
            QueryXml = TaskXml(command: ExpectedCommand()),
            MutatingExitCode = 0,
        });

        var result = await service.RemoveRegistrationAsync();

        result.Succeeded.Should().BeFalse();
        result.ActualState.Should().Be(AutostartState.Enabled);
        result.ErrorMessage.Should().Contain("still present");
    }

    [Fact]
    public async Task RemoveRegistration_DeleteNonZero_QueryMisconfigured_ReportsFailed_NotWeakened()
    {
        // The /Delete failed and the follow-up read yields a Misconfigured task (not Absent):
        // verification invariants are never relaxed just to let the recovery report success.
        var service = NewService(new Script
        {
            QueryExitCode = 0,
            QueryXml = TaskXml(command: @"C:\Other\app.exe"),
            MutatingExitCode = 1,
        });

        var result = await service.RemoveRegistrationAsync();

        result.Succeeded.Should().BeFalse();
        result.ActualState.Should().Be(AutostartState.Misconfigured);
    }

    [Fact]
    public async Task RemoveRegistration_QueryUnknown_ReportsFailed()
    {
        // The follow-up read cannot verify Absent (access denied): fail closed, never report the
        // removal as verified.
        var service = NewService(new Script
        {
            QueryExitCode = 1,
            QueryError = "ERROR: Access is denied.",
            MutatingExitCode = 0,
        });

        var result = await service.RemoveRegistrationAsync();

        result.Succeeded.Should().BeFalse();
        result.ActualState.Should().Be(AutostartState.Unknown);
        result.ErrorMessage.Should().Contain("could not be verified");
    }

    [Fact]
    public async Task RemoveRegistration_InvocationFailure_ReportsFailed()
    {
        var service = NewService(new Script
        {
            QueryExitCode = 1,
            QueryError = "ERROR: The system cannot find the file specified.",
            MutatingExitCode = 0,
            ThrowOnMutate = new InvalidOperationException("schtasks.exe could not be started"),
        });

        var result = await service.RemoveRegistrationAsync();

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("schtasks.exe could not be started");
    }

    // ---------------------------------------------------------------------
    // Structured Misconfigured diagnostics via the seam (full /Query /XML -> typed state)
    // ---------------------------------------------------------------------

    // A realistic /XML export (RegistrationInfo, full Settings with IdleSettings,
    // <Actions Context="Author">, quoted-command round-trip) so the detailed read is verified
    // against the real Windows Task Scheduler shape, not a hand-simplified structure.
    private static string RealXmlWith(
        string? command = null,
        string? arguments = "--background",
        bool logonTrigger = true,
        string? logonType = "InteractiveToken",
        string? runLevel = "HighestAvailable",
        string? enabled = "true")
    {
        var trigger = logonTrigger
            ? "<LogonTrigger><Enabled>true</Enabled><UserId>DESKTOP-TEST\\admin</UserId></LogonTrigger>"
            : "<BootTrigger><Enabled>true</Enabled></BootTrigger>";
        var commandEl = command is null ? string.Empty : $"<Command>{command}</Command>";
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Date>2026-10-25T09:30:00.1234567</Date>
                <Author>DESKTOP-TEST\admin</Author>
                <Description>ProcessGuardian elevated autostart</Description>
                <URI>\ProcessGuardianAdmin</URI>
              </RegistrationInfo>
              <Triggers>
                {trigger}
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>DESKTOP-TEST\admin</UserId>
                  <LogonType>{logonType}</LogonType>
                  <RunLevel>{runLevel}</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>true</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                  <IdleDuration>PT10M</IdleDuration>
                  <WaitTimeout>PT1H</WaitTimeout>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>{enabled}</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT72H</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  {commandEl}
                  <Arguments>{arguments}</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    [Fact]
    public async Task ReadStateDetailed_RealQuotedXml_ReturnsEnabledStateAndParsedDescriptor()
    {
        var service = NewService(new Script
        {
            QueryExitCode = 0,
            QueryXml = RealXmlWith(command: $"\"{ExpectedCommand()}\""),
            MutatingExitCode = 0,
        });

        var (state, descriptor) = await service.ReadStateDetailedAsync();

        state.Should().Be(AutostartState.Enabled);
        descriptor.Should().NotBeNull();
        ScheduledTaskAutostartService.NormalizeCommand(descriptor!.Value.Command).Should().Be(ExpectedCommand());
        ScheduledTaskAutostartService.GetMismatches(descriptor.Value, ExpectedCommand()).Should().BeEmpty();
    }

    [Fact]
    public async Task ReadStateDetailed_MisconfiguredXml_ReturnsDescriptorForPerCheckDiagnostic()
    {
        var service = NewService(new Script
        {
            QueryExitCode = 0,
            QueryXml = RealXmlWith(command: ExpectedCommand(), runLevel: "LeastPrivilege"),
            MutatingExitCode = 0,
        });

        var (state, descriptor) = await service.ReadStateDetailedAsync();

        state.Should().Be(AutostartState.Misconfigured);
        descriptor.Should().NotBeNull();
        var m = ScheduledTaskAutostartService.GetMismatches(descriptor!.Value, ExpectedCommand())
            .Should().ContainSingle().Subject;
        m.Check.Should().Be("RunLevel");
        m.Expected.Should().Be("HighestAvailable");
        m.Actual.Should().Be("LeastPrivilege");
    }

    [Fact]
    public async Task Enable_CreateSucceedsButVerificationIsMisconfigured_ReportsFailed_NotWeakened()
    {
        // Absent -> create exits 0, but the post-operation verification returns a task with a
        // wrong logon type: the enable must still fail as Misconfigured. The security/verification
        // invariants are never relaxed just to let the checkbox turn on.
        var verifyCalled = false;
        ScheduledTaskAutostartService.SchTasksInvoker invoker = (arguments, _) =>
        {
            if (arguments.Contains("/Query", StringComparison.Ordinal))
            {
                if (verifyCalled)
                    return System.Threading.Tasks.Task.FromResult(
                        new ScheduledTaskAutostartService.SchTasksCommandResult(
                            0, RealXmlWith(command: ExpectedCommand(), logonType: "Password"), string.Empty, null));
                verifyCalled = true;
                return System.Threading.Tasks.Task.FromResult(
                    new ScheduledTaskAutostartService.SchTasksCommandResult(1, string.Empty, "ERROR: The system cannot find the file specified.", null));
            }
            return System.Threading.Tasks.Task.FromResult(
                new ScheduledTaskAutostartService.SchTasksCommandResult(0, "SUCCESS", string.Empty, null));
        };
        var service = new ScheduledTaskAutostartService(NullLogger<ScheduledTaskAutostartService>.Instance, invoker);

        var result = await service.SetEnabledAsync(true);

        result.Succeeded.Should().BeFalse();
        result.ActualState.Should().Be(AutostartState.Misconfigured);
        result.ErrorMessage.Should().Contain("not correctly registered");
    }
}
