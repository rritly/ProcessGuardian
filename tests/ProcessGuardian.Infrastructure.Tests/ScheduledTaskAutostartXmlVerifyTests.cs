using System.Diagnostics;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ProcessGuardian.Domain.Enums;
using ProcessGuardian.Infrastructure.Autostart;
using Xunit;

namespace ProcessGuardian.Infrastructure.Tests;

// R-AS-XML-VERIFY regression tests for the Task Scheduler schema default of the optional
// <Settings/Enabled> element. The verbatim fixture below is a real /Query /XML export of a
// successfully created autostart task, captured on a live Windows machine (2026-11-05): the
// task EXISTS and the query exits 0, yet the export contains NO <Enabled> element inside
// <Settings>. Per the Task Scheduler schema the value defaults to true in that case, so the
// task must verify as Enabled — the previous parser left the flag null and Evaluate reported
// Unknown, which made SetEnabledAsync(true) report a successfully created task as unverifiable.
// Fail-closed cases are pinned as well: an unparsable <Enabled> boolean stays Unknown, and a
// task without any <Settings> element is not covered by the schema default. All invocations
// run through the SchTasksInvoker seam; no real scheduled task is touched.
public class ScheduledTaskAutostartXmlVerifyTests
{
    // Verbatim real Windows export (user-reported live evidence, 2026-11-05): /Query /XML of the
    // created ProcessGuardianAdmin task, exit code 0. No <Enabled> element inside <Settings>.
    private const string RealWindowsTaskXml = """
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Date>2026-11-05T00:50:24</Date>
            <Author>ROMM77\rrist</Author>
            <URI>\ProcessGuardianAdmin</URI>
          </RegistrationInfo>
          <Principals>
            <Principal id="Author">
              <UserId>S-1-5-21-804275655-1360264702-1725723139-1001</UserId>
              <LogonType>InteractiveToken</LogonType>
              <RunLevel>HighestAvailable</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <DisallowStartIfOnBatteries>true</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>true</StopIfGoingOnBatteries>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <IdleSettings>
              <Duration>PT10M</Duration>
              <WaitTimeout>PT1H</WaitTimeout>
              <StopOnIdleEnd>true</StopOnIdleEnd>
              <RestartOnIdle>false</RestartOnIdle>
            </IdleSettings>
          </Settings>
          <Triggers>
            <LogonTrigger>
              <StartBoundary>2026-11-05T00:50:00</StartBoundary>
            </LogonTrigger>
          </Triggers>
          <Actions Context="Author">
            <Exec>
              <Command>"C:\Users\rrist\Projects\ProcessGuardian-publish\publish\self-contained-admin\ProcessGuardianAdmin.exe"</Command>
              <Arguments>--background</Arguments>
            </Exec>
          </Actions>
        </Task>
        """;

    // The unquoted executable path the real export points at (the export round-trips the quoted
    // /TR value into a quoted <Command>; NormalizeCommand strips the paired outer quotes).
    private const string RealWindowsTaskExePath =
        @"C:\Users\rrist\Projects\ProcessGuardian-publish\publish\self-contained-admin\ProcessGuardianAdmin.exe";

    private static string ExpectedCommand() =>
        Process.GetCurrentProcess().MainModule?.FileName ?? "C:\\Unknown\\app.exe";

    // Injects an explicit <Enabled> element as the first child of <Settings> in the real export.
    private static string RealWindowsTaskXmlWithEnabled(string value) =>
        RealWindowsTaskXml.Replace("<Settings>", $"<Settings>{Environment.NewLine}    <Enabled>{value}</Enabled>", StringComparison.Ordinal);

    // Overrides the battery power flags of a real-export variant (R-AS-POWER-POLICY). The
    // verbatim fixture keeps both flags true — the live evidence of the /TR-created task — so
    // battery-compliant variants are derived explicitly.
    private static string WithPowerPolicy(string xml, string disallow, string stop) =>
        xml
            .Replace("<DisallowStartIfOnBatteries>true</DisallowStartIfOnBatteries>",
                $"<DisallowStartIfOnBatteries>{disallow}</DisallowStartIfOnBatteries>", StringComparison.Ordinal)
            .Replace("<StopIfGoingOnBatteries>true</StopIfGoingOnBatteries>",
                $"<StopIfGoingOnBatteries>{stop}</StopIfGoingOnBatteries>", StringComparison.Ordinal);

    private static string RealWindowsTaskXmlWithoutSettingsBlock() =>
        Regex.Replace(
            RealWindowsTaskXml,
            @"\s*<Settings>.*?</Settings>\s*",
            string.Empty,
            RegexOptions.Singleline);

    // Minimal capturing logger for the unparsable-<Enabled> diagnostic assertion.
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
    // Real Windows export (no <Settings/Enabled> element)
    // ------------------------------------------------------------------

    [Fact]
    public void ParseTaskXml_RealWindowsExportWithoutEnabledElement_DescriptorIsEnabledTrue()
    {
        var d = ScheduledTaskAutostartService.ParseTaskXml(RealWindowsTaskXml);

        d.Should().NotBeNull("the real export is a parsable task definition");
        d!.Value.Enabled.Should().BeTrue(
            "the Task Scheduler schema default for the optional <Settings/Enabled> is true");
        d.Value.HasLogonTrigger.Should().BeTrue();
        d.Value.LogonType.Should().Be("InteractiveToken");
        d.Value.RunLevel.Should().Be("HighestAvailable");
        d.Value.Arguments.Should().Contain("--background");
        // R-AS-POWER-POLICY: the battery flags of the live /TR-created export are parsed and
        // surface as explicitly true — the evidence of the power-policy defect.
        d.Value.DisallowStartIfOnBatteries.Should().BeTrue(
            "the live export proves the task was created with DisallowStartIfOnBatteries=true");
        d.Value.StopIfGoingOnBatteries.Should().BeTrue(
            "the live export proves the task was created with StopIfGoingOnBatteries=true");
    }

    [Fact]
    public void ParseTaskXml_RealWindowsExportWithoutEnabledElement_BatteryRestrictionsAreTheOnlyMismatches()
    {
        // [R-AS-POWER-POLICY] The verbatim real export (both battery flags true) must fail the
        // two battery power checks — and nothing else. Before R-AS-POWER-POLICY this task was
        // (wrongly) considered fully compliant.
        var d = ScheduledTaskAutostartService.ParseTaskXml(RealWindowsTaskXml)!.Value;

        ScheduledTaskAutostartService.NormalizeCommand(d.Command).Should().Be(RealWindowsTaskExePath);
        var mismatches = ScheduledTaskAutostartService.GetMismatches(d, RealWindowsTaskExePath);
        mismatches.Should().HaveCount(2, "only the two battery power restrictions violate the policy");
        mismatches.Select(m => m.Check).Should().BeEquivalentTo(
            ["DisallowStartIfOnBatteries", "StopIfGoingOnBatteries"]);
        mismatches.Should().OnlyContain(m => m.Expected == "false" && m.Actual == "True");
    }

    [Fact]
    public void Evaluate_RealWindowsExportWithoutEnabledElement_IsMisconfigured_BatteryRestrictionsTrue()
    {
        // [R-AS-POWER-POLICY] The live /TR-created task (battery restrictions true) is
        // Misconfigured even though it is enabled and every other invariant matches.
        var d = ScheduledTaskAutostartService.ParseTaskXml(RealWindowsTaskXml)!.Value;

        ScheduledTaskAutostartService.Evaluate(d, RealWindowsTaskExePath)
            .Should().Be(AutostartState.Misconfigured,
                "a battery-restricted task cannot be trusted as the correct autostart entry");
    }

    // ------------------------------------------------------------------
    // Explicit <Enabled> values against the real export shape
    // ------------------------------------------------------------------

    [Fact]
    public void Evaluate_ExplicitEnabledTrue_IsEnabled()
    {
        var d = ScheduledTaskAutostartService.ParseTaskXml(
            WithPowerPolicy(RealWindowsTaskXmlWithEnabled("true"), "false", "false"))!.Value;

        ScheduledTaskAutostartService.Evaluate(d, RealWindowsTaskExePath)
            .Should().Be(AutostartState.Enabled);
    }

    [Fact]
    public void Evaluate_ExplicitEnabledFalse_IsDisabled()
    {
        var d = ScheduledTaskAutostartService.ParseTaskXml(
            WithPowerPolicy(RealWindowsTaskXmlWithEnabled("false"), "false", "false"))!.Value;

        ScheduledTaskAutostartService.Evaluate(d, RealWindowsTaskExePath)
            .Should().Be(AutostartState.Disabled);
    }

    // ------------------------------------------------------------------
    // Fail-closed cases
    // ------------------------------------------------------------------

    [Fact]
    public void Evaluate_UnparsableEnabledValue_StaysUnknown_AndLogsBoundedDiagnostic()
    {
        var logger = new CapturingLogger();
        // The battery flags are set to the compliant policy so this test isolates the Enabled
        // failure mode (the verbatim export itself is Misconfigured by battery policy since
        // R-AS-POWER-POLICY).
        var d = ScheduledTaskAutostartService.ParseTaskXml(
            WithPowerPolicy(RealWindowsTaskXmlWithEnabled("maybe"), "false", "false"), logger)!.Value;

        d.Enabled.Should().BeNull("an unparsable boolean must never be guessed");
        ScheduledTaskAutostartService.Evaluate(d, RealWindowsTaskExePath)
            .Should().Be(AutostartState.Unknown, "fail-closed: an unreadable Enabled value stays Unknown");

        logger.Warnings.Should().ContainSingle("the invalid value is diagnosed, not swallowed");
        var warning = logger.Warnings.Single();
        warning.Should().Contain("maybe", "the raw invalid element value is logged");
        warning.Should().NotContain("LogonTrigger", "the full task XML is never logged");
        warning.Should().NotContain("Principal", "the full task XML is never logged");
    }

    [Fact]
    public void Evaluate_RealWindowsExportWithoutSettingsElement_IsMisconfigured_NotAutoEnabled()
    {
        // The <Settings/Enabled> schema-default rule covers only a missing <Enabled> inside an
        // existing <Settings> element. A task without any <Settings> block has no verified
        // enabled flag AND no verifiable battery policy — the missing battery flags are a
        // mismatch (R-AS-POWER-POLICY), so the task is Misconfigured, never auto-Enabled.
        var d = ScheduledTaskAutostartService.ParseTaskXml(
            RealWindowsTaskXmlWithoutSettingsBlock())!;

        d.Should().NotBeNull("the task itself is still parsable without a Settings block");
        d!.Value.Enabled.Should().BeNull("no <Settings> element means no verified enabled value");
        ScheduledTaskAutostartService.Evaluate(d.Value, RealWindowsTaskExePath)
            .Should().Be(AutostartState.Misconfigured);
    }

    // ------------------------------------------------------------------
    // Integration through the SchTasksInvoker seam (no real task is touched)
    // ------------------------------------------------------------------

    [Fact]
    public async Task Enable_FromAbsent_CreateExitsZero_VerifyQueryXmlWithoutEnabledElement_ReportsSuccessEnabled()
    {
        // The full SetEnabledAsync(true) flow against the real export shape: the pre-state read
        // finds the task absent, /Create /XML exits 0, and the post-operation /Query /XML returns
        // an export WITHOUT a <Settings/Enabled> element and WITH both battery flags explicitly
        // false — exactly what the R-AS-POWER-POLICY XML import produces (the schema default for
        // a battery flag is true, so the created XML sets both explicitly). In reality the
        // created task points at the current executable, so the verification export's <Command>
        // is substituted with the current process path; the rest of the export stays verbatim.
        // The enable must succeed with the verified Enabled state — not report the task as
        // unverifiable or Misconfigured.
        var verificationXml = WithPowerPolicy(
            RealWindowsTaskXml.Replace(RealWindowsTaskExePath, ExpectedCommand(), StringComparison.Ordinal),
            "false", "false");
        var queryCalls = 0;
        ScheduledTaskAutostartService.SchTasksInvoker invoker = (arguments, _) =>
        {
            if (arguments.Contains("/Query", StringComparison.Ordinal))
            {
                queryCalls++;
                return queryCalls == 1
                    ? System.Threading.Tasks.Task.FromResult(new ScheduledTaskAutostartService.SchTasksCommandResult(
                        1, string.Empty, "ERROR: The system cannot find the file specified.", null))
                    : System.Threading.Tasks.Task.FromResult(new ScheduledTaskAutostartService.SchTasksCommandResult(
                        0, verificationXml, string.Empty, null));
            }

            // /Create: a real successful create exits 0.
            return System.Threading.Tasks.Task.FromResult(new ScheduledTaskAutostartService.SchTasksCommandResult(
                0, "SUCCESS: The scheduled task \"ProcessGuardianAdmin\" has been successfully created.", string.Empty, null));
        };
        var service = new ScheduledTaskAutostartService(
            NullLogger<ScheduledTaskAutostartService>.Instance, invoker, new FakeStagingStore());

        var result = await service.SetEnabledAsync(true);

        result.Succeeded.Should().BeTrue();
        result.ActualState.Should().Be(AutostartState.Enabled,
            "the post-create /Query /XML verification must honor the schema default for the optional Enabled element");
        queryCalls.Should().Be(2, "exactly the pre-state read and the post-operation verification query");
    }
}
