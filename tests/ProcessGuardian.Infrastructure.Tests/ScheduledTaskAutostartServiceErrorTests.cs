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

        public (int ExitCode, string Output) For(string arguments)
        {
            if (arguments.Contains("/Query", StringComparison.Ordinal))
                return QueryExitCode == 0
                    ? (0, QueryXml)
                    : (QueryExitCode, QueryError);

            if (ThrowOnMutate is { } ex)
                throw ex;
            return (MutatingExitCode, MutatingExitCode == 0 ? "SUCCESS" : "ERROR: The parameter is incorrect.");
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
    public async Task ReadState_MissingEnabledFlag_IsUnknown_NotEnabled()
    {
        // Build XML without the <Enabled> element by stripping it from a parseable document.
        var xml = TaskXml(command: ExpectedCommand(), enabled: "true")
            .Replace("<Enabled>true</Enabled>", string.Empty, StringComparison.Ordinal);
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
            return System.Threading.Tasks.Task.FromResult((1, "ERROR: Access is denied."));
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
                    return System.Threading.Tasks.Task.FromResult((0, TaskXml(command: ExpectedCommand())));
                verifyCalled = true;
                return System.Threading.Tasks.Task.FromResult((1, "ERROR: The system cannot find the file specified."));
            }
            return System.Threading.Tasks.Task.FromResult((0, "SUCCESS"));
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
                    return System.Threading.Tasks.Task.FromResult((1, "ERROR: The system cannot find the file specified."));
                verifyCalled = true;
                return System.Threading.Tasks.Task.FromResult((1, "ERROR: The system cannot find the file specified."));
            }
            return System.Threading.Tasks.Task.FromResult((0, "SUCCESS"));
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
            return System.Threading.Tasks.Task.FromResult((1, "ERROR: The system cannot find the file specified."));
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
                        (0, RealXmlWith(command: ExpectedCommand(), logonType: "Password")));
                verifyCalled = true;
                return System.Threading.Tasks.Task.FromResult((1, "ERROR: The system cannot find the file specified."));
            }
            return System.Threading.Tasks.Task.FromResult((0, "SUCCESS"));
        };
        var service = new ScheduledTaskAutostartService(NullLogger<ScheduledTaskAutostartService>.Instance, invoker);

        var result = await service.SetEnabledAsync(true);

        result.Succeeded.Should().BeFalse();
        result.ActualState.Should().Be(AutostartState.Misconfigured);
        result.ErrorMessage.Should().Contain("not correctly registered");
    }
}
