using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ProcessGuardian.Domain;
using ProcessGuardian.Domain.Enums;
using ProcessGuardian.Infrastructure.Autostart;
using Xunit;

namespace ProcessGuardian.Infrastructure.Tests;

// R-AS-POWER-POLICY regression tests: the ProcessGuardianAdmin autostart task must be
// independent of the power source. Both Task Scheduler power restrictions —
// <DisallowStartIfOnBatteries> and <StopIfGoingOnBatteries> — must be explicitly false in the
// registered task, must be verified against the exported task XML (a missing or unparsable
// value is a mismatch, never proof of compliance: Windows defaults both flags to true), and a
// task with the wrong policy must be classified as Misconfigured and repaired by the existing
// SetEnabledAsync path. The definition XML is registered via "schtasks /Create /XML" import —
// the command line has no switches for these settings and would leave both at their true
// defaults (live evidence: the previously created task exported both flags as true). All
// invocations run through the SchTasksInvoker seam; no real scheduled task is touched.
public class ScheduledTaskAutostartPowerPolicyTests
{
    // A realistic task export with controllable battery power flags. Defaults match the required
    // explicit-false power policy; null omits an element, any other string overrides its value.
    private static string TaskXml(
        string? command = null,
        string arguments = "--background",
        string? disallow = "false",
        string? stop = "false",
        string? enabled = "true")
    {
        var disallowEl = disallow is null
            ? string.Empty
            : $"<DisallowStartIfOnBatteries>{disallow}</DisallowStartIfOnBatteries>";
        var stopEl = stop is null
            ? string.Empty
            : $"<StopIfGoingOnBatteries>{stop}</StopIfGoingOnBatteries>";
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Triggers>
                <LogonTrigger />
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Actions>
                <Exec>
                  <Command>{command}</Command>
                  <Arguments>{arguments}</Arguments>
                </Exec>
              </Actions>
              <Settings>
                {disallowEl}
                {stopEl}
                <Enabled>{enabled}</Enabled>
              </Settings>
            </Task>
            """;
    }

    private static string ExpectedCommand() =>
        Process.GetCurrentProcess().MainModule?.FileName ?? "C:\\Unknown\\app.exe";

    // Minimal capturing logger for the unparsable-battery-value diagnostic assertion.
    private sealed class CapturingLogger : ILogger
    {
        public List<string> Warnings { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel == LogLevel.Warning;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }
    }

    // ------------------------------------------------------------------
    // XML parsing (scenarios 1-7)
    // ------------------------------------------------------------------

    [Fact]
    public void ParseTaskXml_BothBatteryFlagsExplicitFalse_ParsedAsFalse()
    {
        var d = ScheduledTaskAutostartService.ParseTaskXml(TaskXml(command: ExpectedCommand()))!.Value;

        d.DisallowStartIfOnBatteries.Should().BeFalse();
        d.StopIfGoingOnBatteries.Should().BeFalse();
    }

    [Fact]
    public void ParseTaskXml_DisallowStartIfOnBatteriesTrue_ParsedAsTrue()
    {
        var d = ScheduledTaskAutostartService.ParseTaskXml(
            TaskXml(command: ExpectedCommand(), disallow: "true"))!.Value;

        d.DisallowStartIfOnBatteries.Should().BeTrue();
        d.StopIfGoingOnBatteries.Should().BeFalse();
    }

    [Fact]
    public void ParseTaskXml_StopIfGoingOnBatteriesTrue_ParsedAsTrue()
    {
        var d = ScheduledTaskAutostartService.ParseTaskXml(
            TaskXml(command: ExpectedCommand(), stop: "true"))!.Value;

        d.DisallowStartIfOnBatteries.Should().BeFalse();
        d.StopIfGoingOnBatteries.Should().BeTrue();
    }

    [Fact]
    public void ParseTaskXml_MissingDisallowStartIfOnBatteries_IsNull()
    {
        // No schema default is applied for the battery flags: Windows defaults both to true, so
        // a missing element must stay unverified (null), never auto-false.
        var d = ScheduledTaskAutostartService.ParseTaskXml(
            TaskXml(command: ExpectedCommand(), disallow: null))!.Value;

        d.DisallowStartIfOnBatteries.Should().BeNull("a missing flag is never proof of compliance");
        d.StopIfGoingOnBatteries.Should().BeFalse();
    }

    [Fact]
    public void ParseTaskXml_MissingStopIfGoingOnBatteries_IsNull()
    {
        var d = ScheduledTaskAutostartService.ParseTaskXml(
            TaskXml(command: ExpectedCommand(), stop: null))!.Value;

        d.DisallowStartIfOnBatteries.Should().BeFalse();
        d.StopIfGoingOnBatteries.Should().BeNull("a missing flag is never proof of compliance");
    }

    [Fact]
    public void ParseTaskXml_UnparsableBatteryValue_IsNull_AndLogsBoundedDiagnostic()
    {
        var logger = new CapturingLogger();
        var d = ScheduledTaskAutostartService.ParseTaskXml(
            TaskXml(command: ExpectedCommand(), disallow: "on", stop: "yes"), logger)!.Value;

        d.DisallowStartIfOnBatteries.Should().BeNull("an unparsable boolean must never be guessed");
        d.StopIfGoingOnBatteries.Should().BeNull("an unparsable boolean must never be guessed");

        logger.Warnings.Should().HaveCount(2, "each unparsable battery flag is diagnosed, not swallowed");
        logger.Warnings.Should().Contain(w => w.Contains("on", StringComparison.Ordinal));
        logger.Warnings.Should().Contain(w => w.Contains("yes", StringComparison.Ordinal));
        logger.Warnings.Should().NotContain(w => w.Contains("LogonTrigger", StringComparison.Ordinal),
            "the full task XML is never logged");
    }

    [Fact]
    public void ParseTaskXml_UnparsableXml_ReturnsNull()
    {
        // Scenario 7: XML that cannot be read at all keeps the existing fail-closed semantics —
        // the parser returns null and the typed state stays Unknown (a read error is never
        // masked as a successful verification).
        ScheduledTaskAutostartService.ParseTaskXml("not xml at all").Should().BeNull();
    }

    // ------------------------------------------------------------------
    // State evaluation (scenarios 8-10)
    // ------------------------------------------------------------------

    [Fact]
    public void Evaluate_CompliantTaskWithEnabledTrue_IsEnabled()
    {
        var d = ScheduledTaskAutostartService.ParseTaskXml(TaskXml(command: ExpectedCommand()))!.Value;

        ScheduledTaskAutostartService.Evaluate(d, ExpectedCommand())
            .Should().Be(AutostartState.Enabled);
    }

    [Fact]
    public void Evaluate_EnabledTaskWithDisallowStartIfOnBatteriesTrue_IsMisconfigured()
    {
        var d = ScheduledTaskAutostartService.ParseTaskXml(
            TaskXml(command: ExpectedCommand(), disallow: "true"))!.Value;

        ScheduledTaskAutostartService.Evaluate(d, ExpectedCommand())
            .Should().Be(AutostartState.Misconfigured,
                "a task that cannot start on battery power is not a valid autostart entry");
    }

    [Fact]
    public void Evaluate_EnabledTaskWithStopIfGoingOnBatteriesTrue_IsMisconfigured()
    {
        var d = ScheduledTaskAutostartService.ParseTaskXml(
            TaskXml(command: ExpectedCommand(), stop: "true"))!.Value;

        ScheduledTaskAutostartService.Evaluate(d, ExpectedCommand())
            .Should().Be(AutostartState.Misconfigured,
                "a task that stops when the machine switches to battery is not a valid autostart entry");
    }

    [Fact]
    public void Evaluate_BatteryMismatch_IsNotMaskedByOtherCorrectParameters()
    {
        // Scenario 10: every other invariant is correct, both battery flags violate the policy —
        // the task must still be Misconfigured, with both failing checks reported.
        var d = ScheduledTaskAutostartService.ParseTaskXml(
            TaskXml(command: ExpectedCommand(), disallow: "true", stop: "true"))!.Value;

        ScheduledTaskAutostartService.Evaluate(d, ExpectedCommand())
            .Should().Be(AutostartState.Misconfigured);

        var mismatches = ScheduledTaskAutostartService.GetMismatches(d, ExpectedCommand());
        mismatches.Should().HaveCount(2, "both battery power restrictions violate the policy");
        mismatches.Select(m => m.Check).Should().BeEquivalentTo(
            ["DisallowStartIfOnBatteries", "StopIfGoingOnBatteries"]);
    }

    [Fact]
    public void GetMismatches_BatteryFlagTrue_ReportsExactCheckExpectedActual()
    {
        var d = ScheduledTaskAutostartService.ParseTaskXml(
            TaskXml(command: ExpectedCommand(), disallow: "true", stop: "true"))!.Value;

        var mismatches = ScheduledTaskAutostartService.GetMismatches(d, ExpectedCommand());
        mismatches.Should().OnlyContain(m => m.Expected == "false" && m.Actual == "True");
    }

    [Fact]
    public void GetMismatches_MissingBatteryFlag_ReportsMissingActual()
    {
        // A missing element is a mismatch, not compliance: absence never proves the task is
        // unrestricted.
        var d = ScheduledTaskAutostartService.ParseTaskXml(
            TaskXml(command: ExpectedCommand(), disallow: null, stop: null))!.Value;

        var mismatches = ScheduledTaskAutostartService.GetMismatches(d, ExpectedCommand());
        mismatches.Should().HaveCount(2);
        mismatches.Should().OnlyContain(m => m.Expected == "false" && m.Actual == "<missing>");
    }

    [Fact]
    public void GetMismatches_UnparsableBatteryValue_ReportsMissingActual()
    {
        var d = ScheduledTaskAutostartService.ParseTaskXml(
            TaskXml(command: ExpectedCommand(), disallow: "on"))!.Value;

        var m = ScheduledTaskAutostartService.GetMismatches(d, ExpectedCommand())
            .Should().ContainSingle().Subject;
        m.Check.Should().Be("DisallowStartIfOnBatteries");
        m.Expected.Should().Be("false");
        m.Actual.Should().Be("<missing>");
    }

    // ------------------------------------------------------------------
    // Definition XML built for registration (scenario 12)
    // ------------------------------------------------------------------

    [Fact]
    public void BuildTaskDefinitionXml_SetsBothBatteryRestrictionsExplicitlyFalse()
    {
        var xml = ScheduledTaskAutostartService.BuildTaskDefinitionXml(ExpectedCommand());

        xml.Should().Contain("<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>");
        xml.Should().Contain("<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>");
    }

    [Fact]
    public void BuildTaskDefinitionXml_PreservesAllAutostartInvariants()
    {
        // The built definition round-trips through the production parser: LogonTrigger,
        // InteractiveToken, HighestAvailable, the quoted executable with --background, and both
        // battery flags false — the exact invariants GetMismatches verifies.
        const string exe = @"C:\My Tools\ProcessGuardianAdmin.exe";
        var xml = ScheduledTaskAutostartService.BuildTaskDefinitionXml(exe);

        var d = ScheduledTaskAutostartService.ParseTaskXml(xml);

        d.Should().NotBeNull("the definition built for import must parse back");
        d!.Value.HasLogonTrigger.Should().BeTrue();
        d.Value.LogonType.Should().Be("InteractiveToken");
        d.Value.RunLevel.Should().Be("HighestAvailable");
        d.Value.Arguments.Should().Contain("--background");
        ScheduledTaskAutostartService.NormalizeCommand(d.Value.Command).Should().Be(exe);
        d.Value.DisallowStartIfOnBatteries.Should().BeFalse();
        d.Value.StopIfGoingOnBatteries.Should().BeFalse();
        d.Value.Enabled.Should().BeTrue();

        ScheduledTaskAutostartService.GetMismatches(d.Value, exe).Should().BeEmpty();
        ScheduledTaskAutostartService.Evaluate(d.Value, exe).Should().Be(AutostartState.Enabled);
    }

    [Fact]
    public void BuildTaskDefinitionXml_EscapesXmlSpecialCharacters_InExecutablePath()
    {
        const string exe = @"C:\Odd & Space\ProcessGuardianAdmin.exe";
        var xml = ScheduledTaskAutostartService.BuildTaskDefinitionXml(exe);

        xml.Should().Contain("&amp;", "the executable path is escaped, not raw");
        var d = ScheduledTaskAutostartService.ParseTaskXml(xml);
        d.Should().NotBeNull();
        ScheduledTaskAutostartService.NormalizeCommand(d!.Value.Command).Should().Be(exe);
    }

    [Fact]
    public void BuildTaskDefinitionXml_HasExactlyOneXmlDeclaration_MatchingTheWrittenEncoding()
    {
        var xml = ScheduledTaskAutostartService.BuildTaskDefinitionXml(ExpectedCommand());

        Regex.Matches(xml, "<\\?xml").Should().HaveCount(1,
            "a duplicated declaration would make the import fail");
        xml.Should().Contain("encoding=\"utf-16\"",
            "the declaration must match the Encoding.Unicode bytes the file is written with");
    }

    [Fact]
    public void BuildTaskDefinitionXml_UserIdIsTheCurrentIdentity_ResolvedAtRuntime()
    {
        var xml = ScheduledTaskAutostartService.BuildTaskDefinitionXml(ExpectedCommand());

        xml.Should().Contain($"{Environment.UserDomainName}\\{Environment.UserName}",
            "the principal is the identity the process actually runs under, never a hard-coded name");
    }

    // ------------------------------------------------------------------
    // Registration, repair and verification (scenarios 11, 13, 14)
    // ------------------------------------------------------------------

    [Fact]
    public async Task Enable_EnabledTaskWithBatteryRestriction_IsReRegisteredWithCompliantDefinition()
    {
        // Scenario 11: an already-enabled task whose battery policy is wrong is Misconfigured —
        // SetEnabledAsync(true) must NOT short-circuit it as "already enabled": it must
        // re-register through the existing repair path (/Create with /F) with a compliant
        // definition.
        var compliantVerificationXml = TaskXml(command: ExpectedCommand());
        var queryCalls = 0;
        string? createArguments = null;
        ScheduledTaskAutostartService.SchTasksInvoker invoker = (arguments, _) =>
        {
            if (arguments.Contains("/Query", StringComparison.Ordinal))
            {
                queryCalls++;
                return queryCalls == 1
                    ? System.Threading.Tasks.Task.FromResult(
                        new ScheduledTaskAutostartService.SchTasksCommandResult(
                            0, TaskXml(command: ExpectedCommand(), disallow: "true", stop: "true"),
                            string.Empty, null))
                    : System.Threading.Tasks.Task.FromResult(
                        new ScheduledTaskAutostartService.SchTasksCommandResult(
                            0, compliantVerificationXml, string.Empty, null));
            }

            createArguments = arguments;
            return System.Threading.Tasks.Task.FromResult(
                new ScheduledTaskAutostartService.SchTasksCommandResult(0, "SUCCESS", string.Empty, null));
        };
        var service = new ScheduledTaskAutostartService(
            NullLogger<ScheduledTaskAutostartService>.Instance, invoker, new FakeStagingStore());

        var result = await service.SetEnabledAsync(true);

        result.Succeeded.Should().BeTrue(
            "the pre-state read classifies the battery-restricted task as Misconfigured, so the repair path must run");
        result.ActualState.Should().Be(AutostartState.Enabled);
        queryCalls.Should().Be(2, "the pre-state read and the post-registration verification");
        createArguments.Should().NotBeNull();
        createArguments.Should().Contain("/XML", "the definition is registered by XML import");
        createArguments.Should().Contain("/F", "an existing task is force-overwritten in place");
    }

    [Fact]
    public async Task Enable_CreateExitsZero_VerificationConfirmsBothBatteryFlagsFalse_ReportsSucceeded()
    {
        // Scenario 13: after registration the XML is re-read and both power flags are verified
        // against the actual export; the create exit code alone is never success.
        var compliantVerificationXml = TaskXml(command: ExpectedCommand(), disallow: "false", stop: "false");
        var queryCalls = 0;
        ScheduledTaskAutostartService.SchTasksInvoker invoker = (arguments, _) =>
        {
            if (arguments.Contains("/Query", StringComparison.Ordinal))
            {
                queryCalls++;
                return queryCalls == 1
                    ? System.Threading.Tasks.Task.FromResult(
                        new ScheduledTaskAutostartService.SchTasksCommandResult(
                            1, string.Empty, "ERROR: The system cannot find the file specified.", null))
                    : System.Threading.Tasks.Task.FromResult(
                        new ScheduledTaskAutostartService.SchTasksCommandResult(
                            0, compliantVerificationXml, string.Empty, null));
            }

            return System.Threading.Tasks.Task.FromResult(
                new ScheduledTaskAutostartService.SchTasksCommandResult(0, "SUCCESS", string.Empty, null));
        };
        var service = new ScheduledTaskAutostartService(
            NullLogger<ScheduledTaskAutostartService>.Instance, invoker, new FakeStagingStore());

        var result = await service.SetEnabledAsync(true);

        result.Succeeded.Should().BeTrue();
        result.ActualState.Should().Be(AutostartState.Enabled);
        queryCalls.Should().Be(2);
    }

    [Fact]
    public async Task Enable_CreateExitsZero_VerificationFindsBatteryRestriction_ReportsFailedMisconfigured()
    {
        // The create exits 0 but the re-read export still shows a battery restriction: the
        // enable fails as Misconfigured with diagnostics — the exit code is never proof.
        var queryCalls = 0;
        ScheduledTaskAutostartService.SchTasksInvoker invoker = (arguments, _) =>
        {
            if (arguments.Contains("/Query", StringComparison.Ordinal))
            {
                queryCalls++;
                return queryCalls == 1
                    ? System.Threading.Tasks.Task.FromResult(
                        new ScheduledTaskAutostartService.SchTasksCommandResult(
                            1, string.Empty, "ERROR: The system cannot find the file specified.", null))
                    : System.Threading.Tasks.Task.FromResult(
                        new ScheduledTaskAutostartService.SchTasksCommandResult(
                            0, TaskXml(command: ExpectedCommand(), disallow: "true"), string.Empty, null));
            }

            return System.Threading.Tasks.Task.FromResult(
                new ScheduledTaskAutostartService.SchTasksCommandResult(0, "SUCCESS", string.Empty, null));
        };
        var service = new ScheduledTaskAutostartService(
            NullLogger<ScheduledTaskAutostartService>.Instance, invoker, new FakeStagingStore());

        var result = await service.SetEnabledAsync(true);

        result.Succeeded.Should().BeFalse();
        result.ActualState.Should().Be(AutostartState.Misconfigured);
        result.ErrorMessage.Should().Contain("not correctly registered");
        queryCalls.Should().Be(2, "the verification re-read the actual XML after the create");
    }

    [Fact]
    public async Task Enable_CreateNonZeroExitCode_ReportsFailedWithDiagnostic()
    {
        // Scenario 14: a create/verify failure is handled by the existing error mechanism.
        var service = new ScheduledTaskAutostartService(
            NullLogger<ScheduledTaskAutostartService>.Instance, (_, _) =>
                System.Threading.Tasks.Task.FromResult(
                    new ScheduledTaskAutostartService.SchTasksCommandResult(
                        1, string.Empty, "ERROR: The system cannot find the file specified.", null)),
            new FakeStagingStore());

        var result = await service.SetEnabledAsync(true);

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("exit 1");
    }

    // The temporary definition file is deleted on success and on failure alike.
    [Fact]
    public async Task Enable_TempDefinitionFileIsDeleted_AfterSuccessfulCreate()
    {
        string? definitionPath = null;
        var compliantVerificationXml = TaskXml(command: ExpectedCommand());
        var queryCalls = 0;
        ScheduledTaskAutostartService.SchTasksInvoker invoker = (arguments, _) =>
        {
            if (arguments.Contains("/Query", StringComparison.Ordinal))
            {
                queryCalls++;
                return queryCalls == 1
                    ? System.Threading.Tasks.Task.FromResult(
                        new ScheduledTaskAutostartService.SchTasksCommandResult(
                            1, string.Empty, "ERROR: The system cannot find the file specified.", null))
                    : System.Threading.Tasks.Task.FromResult(
                        new ScheduledTaskAutostartService.SchTasksCommandResult(
                            0, compliantVerificationXml, string.Empty, null));
            }

            // /Create /XML "<path>": capture the temporary definition file path.
            var match = Regex.Match(arguments, "/XML \"(.+?)\"");
            definitionPath = match.Groups[1].Value;
            return System.Threading.Tasks.Task.FromResult(
                new ScheduledTaskAutostartService.SchTasksCommandResult(0, "SUCCESS", string.Empty, null));
        };
        var store = new FakeStagingStore();
        var service = new ScheduledTaskAutostartService(
            NullLogger<ScheduledTaskAutostartService>.Instance, invoker, store);

        var result = await service.SetEnabledAsync(true);

        result.Succeeded.Should().BeTrue();
        definitionPath.Should().NotBeNullOrEmpty();
        store.Deleted.Should().Contain(definitionPath,
            "the staged definition file is cleaned up after a successful create");
        File.Exists(definitionPath).Should().BeFalse(
            "the staged definition file is cleaned up after a successful create");
    }

    [Fact]
    public async Task Enable_TempDefinitionFileIsDeleted_WhenCreateFails()
    {
        string? definitionPath = null;
        ScheduledTaskAutostartService.SchTasksInvoker invoker = (arguments, _) =>
        {
            if (arguments.Contains("/Query", StringComparison.Ordinal))
                return System.Threading.Tasks.Task.FromResult(
                    new ScheduledTaskAutostartService.SchTasksCommandResult(
                        1, string.Empty, "ERROR: The system cannot find the file specified.", null));

            var match = Regex.Match(arguments, "/XML \"(.+?)\"");
            definitionPath = match.Groups[1].Value;
            return System.Threading.Tasks.Task.FromResult(
                new ScheduledTaskAutostartService.SchTasksCommandResult(1, string.Empty,
                    "ERROR: The parameter is incorrect.", null));
        };
        var store = new FakeStagingStore();
        var service = new ScheduledTaskAutostartService(
            NullLogger<ScheduledTaskAutostartService>.Instance, invoker, store);

        var result = await service.SetEnabledAsync(true);

        result.Succeeded.Should().BeFalse();
        definitionPath.Should().NotBeNullOrEmpty();
        store.Deleted.Should().Contain(definitionPath,
            "the staged definition file is cleaned up even when the create fails");
        File.Exists(definitionPath).Should().BeFalse(
            "the staged definition file is cleaned up even when the create fails");
    }

    // A create/verify failure is handled by the existing error mechanism (scenario 14): the
    // staged definition file is also cleaned up when the invocation throws.
    [Fact]
    public async Task Enable_InvocationThrows_TempDefinitionFileIsDeleted()
    {
        string? definitionPath = null;
        ScheduledTaskAutostartService.SchTasksInvoker invoker = (arguments, _) =>
        {
            if (arguments.Contains("/Query", StringComparison.Ordinal))
                return System.Threading.Tasks.Task.FromResult(
                    new ScheduledTaskAutostartService.SchTasksCommandResult(
                        1, string.Empty, "ERROR: The system cannot find the file specified.", null));

            var match = Regex.Match(arguments, "/XML \"(.+?)\"");
            definitionPath = match.Groups[1].Value;
            throw new InvalidOperationException("boom");
        };
        var store = new FakeStagingStore();
        var service = new ScheduledTaskAutostartService(
            NullLogger<ScheduledTaskAutostartService>.Instance, invoker, store);

        var result = await service.SetEnabledAsync(true);

        result.Succeeded.Should().BeFalse();
        // R-AS-TASK-SCHEDULER-DIAGNOSTICS: the raw exception message stays out of the
        // user-facing ErrorMessage (it remains in the structured log).
        result.ErrorMessage.Should().Be("task creation failed: schtasks.exe could not be started");
        result.ErrorMessage.Should().NotContain("boom");
        definitionPath.Should().NotBeNullOrEmpty();
        store.Deleted.Should().Contain(definitionPath,
            "the finally-block cleanup runs even when the invocation throws");
        File.Exists(definitionPath).Should().BeFalse(
            "the finally-block cleanup runs even when the invocation throws");
    }
}
