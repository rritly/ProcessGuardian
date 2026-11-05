using System.Diagnostics;
using System.IO;
using FluentAssertions;
using ProcessGuardian.Domain;
using ProcessGuardian.Domain.Enums;
using ProcessGuardian.Infrastructure.Autostart;
using Xunit;

namespace ProcessGuardian.Infrastructure.Tests;

// Command-line construction and machine-readable XML parsing/evaluation for schtasks.exe
// (admin edition autostart). The typed state is derived from the task XML, never from
// localized text or a raw exit code.
public class ScheduledTaskAutostartServiceTests
{
    // A valid task export matching the expected autostart registration, with optional overrides.
    // The battery power flags default to the required explicit-false policy (R-AS-POWER-POLICY);
    // pass null to omit an element or a string to override its value.
    private static string TaskXml(
        string? command = null,
        string? arguments = " --background ",
        bool logonTrigger = true,
        string? logonType = "InteractiveToken",
        string? runLevel = "HighestAvailable",
        string? enabled = "true",
        string? disallowStartIfOnBatteries = "false",
        string? stopIfGoingOnBatteries = "false")
    {
        var trigger = logonTrigger ? "<LogonTrigger />" : "<BootTrigger />";
        var disallowEl = disallowStartIfOnBatteries is null
            ? string.Empty
            : $"<DisallowStartIfOnBatteries>{disallowStartIfOnBatteries}</DisallowStartIfOnBatteries>";
        var stopEl = stopIfGoingOnBatteries is null
            ? string.Empty
            : $"<StopIfGoingOnBatteries>{stopIfGoingOnBatteries}</StopIfGoingOnBatteries>";
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
                {disallowEl}
                {stopEl}
                <Enabled>{enabled}</Enabled>
              </Settings>
            </Task>
            """;
    }

    private static string ExpectedCommand() =>
        Process.GetCurrentProcess().MainModule?.FileName ?? "C:\\Unknown\\app.exe";

    // [R-AS-POWER-POLICY] The registration imports the definition XML built by
    // BuildTaskDefinitionXml; /F force-overwrites an existing (e.g. battery-restricted) task.
    [Fact]
    public void BuildCreateArguments_ImportsTaskDefinitionXml_FromTheGivenPath()
    {
        var args = ScheduledTaskAutostartService.BuildCreateArguments(@"C:\Temp\def.xml");

        args.Should().Be(
            $"/Create /TN \"{AppEdition.ScheduledTaskName}\" /XML \"C:\\Temp\\def.xml\" /F");
        args.Should().NotContain("/TR", "the definition comes from the imported XML, not the /TR command line");
    }

    [Fact]
    public void BuildDeleteArguments_DeletesTaskForcefully()
    {
        var args = ScheduledTaskAutostartService.BuildDeleteArguments();

        args.Should().Be($"/Delete /TN \"{AppEdition.ScheduledTaskName}\" /F");
    }

    [Fact]
    public void BuildQueryArguments_QueriesTaskByName()
    {
        var args = ScheduledTaskAutostartService.BuildQueryArguments();

        args.Should().Be($"/Query /TN \"{AppEdition.ScheduledTaskName}\"");
    }

    [Fact]
    public void BuildQueryXmlArguments_RequestsMachineReadableXmlExport()
    {
        var args = ScheduledTaskAutostartService.BuildQueryXmlArguments();

        args.Should().Be($"/Query /TN \"{AppEdition.ScheduledTaskName}\" /XML");
        args.Should().Contain("/XML", "the state must be read from the machine-readable XML, not localized text");
    }

    // Defect regression: schtasks.exe must be invoked via its absolute System32 path so a
    // hostile PATH entry cannot redirect the task registration through other code.
    [Fact]
    public void ResolveSchTasksPath_IsAbsoluteSystem32Path_NotBareName()
    {
        var path = ScheduledTaskAutostartService.ResolveSchTasksPath();

        path.Should().Be(Path.Combine(Environment.SystemDirectory, "schtasks.exe"));
        path.Should().NotBe("schtasks.exe", "a bare name would resolve through the process PATH");
        Path.IsPathRooted(path).Should().BeTrue();
        Path.GetDirectoryName(path).Should().Be(Environment.SystemDirectory);
    }

    [Fact]
    public void BuildProcessStartInfo_UsesAbsoluteSystem32Path_NotBareName()
    {
        var psi = ScheduledTaskAutostartService.BuildProcessStartInfo("/Query");

        psi.FileName.Should().Be(Path.Combine(Environment.SystemDirectory, "schtasks.exe"));
        psi.FileName.Should().NotBe("schtasks.exe");
        psi.UseShellExecute.Should().BeFalse();
        psi.CreateNoWindow.Should().BeTrue();
        psi.Arguments.Should().Be("/Query");
    }

    // ------------------------------------------------------------------
    // ParseTaskXml / Evaluate
    // ------------------------------------------------------------------

    [Fact]
    public void ParseTaskXml_ParsesExpectedConfiguration_ToEnabledDescriptor()
    {
        var descriptor = ScheduledTaskAutostartService.ParseTaskXml(
            TaskXml(command: ExpectedCommand()));

        descriptor.Should().NotBeNull();
        descriptor!.Value.Command.Should().Be(ExpectedCommand());
        descriptor.Value.Arguments.Should().Contain("--background");
        descriptor.Value.HasLogonTrigger.Should().BeTrue();
        descriptor.Value.LogonType.Should().Be("InteractiveToken");
        descriptor.Value.RunLevel.Should().Be("HighestAvailable");
        descriptor.Value.Enabled.Should().BeTrue();
        descriptor.Value.DisallowStartIfOnBatteries.Should().BeFalse();
        descriptor.Value.StopIfGoingOnBatteries.Should().BeFalse();
    }

    [Fact]
    public void ParseTaskXml_InvalidXml_ReturnsNull()
    {
        ScheduledTaskAutostartService.ParseTaskXml("not xml at all").Should().BeNull();
    }

    [Fact]
    public void ParseTaskXml_EmptyXml_ReturnsNull()
    {
        ScheduledTaskAutostartService.ParseTaskXml(null).Should().BeNull();
        ScheduledTaskAutostartService.ParseTaskXml(string.Empty).Should().BeNull();
        ScheduledTaskAutostartService.ParseTaskXml("   ").Should().BeNull();
    }

    [Fact]
    public void ParseTaskXml_WrongRootElement_ReturnsNull()
    {
        var xml = TaskXml(command: ExpectedCommand())
            .Replace("<Task", "<RegistryTask", StringComparison.Ordinal);
        ScheduledTaskAutostartService.ParseTaskXml(xml).Should().BeNull();
    }

    [Fact]
    public void ParseTaskXml_MissingActions_ReturnsDescriptorWithNullCommand()
    {
        var stripped = TaskXml(command: ExpectedCommand());
        // Remove every <Actions> element entirely; the command stays null without an Exec action.
        stripped = System.Text.RegularExpressions.Regex.Replace(
            stripped, @"\s*<Actions>.*?</Actions>\s*", string.Empty, System.Text.RegularExpressions.RegexOptions.Singleline);
        var descriptor = ScheduledTaskAutostartService.ParseTaskXml(stripped);

        descriptor.Should().NotBeNull();
        descriptor!.Value.Command.Should().BeNull("a task without an Exec action has no command");
        descriptor.Value.Arguments.Should().BeNull();
    }

    [Fact]
    public void Evaluate_MatchingConfiguration_IsEnabled()
    {
        var d = ScheduledTaskAutostartService.ParseTaskXml(TaskXml(command: ExpectedCommand()))!.Value;

        ScheduledTaskAutostartService.Evaluate(d, ExpectedCommand()).Should().Be(AutostartState.Enabled);
    }

    [Fact]
    public void Evaluate_DisabledFlagWinsOverNothingElse()
    {
        var d = ScheduledTaskAutostartService.ParseTaskXml(
            TaskXml(command: ExpectedCommand(), enabled: "false"))!.Value;

        ScheduledTaskAutostartService.Evaluate(d, ExpectedCommand()).Should().Be(AutostartState.Disabled);
    }

    [Fact]
    public void Evaluate_IncorrectCommand_IsMisconfiguredEvenWhenEnabled()
    {
        var d = ScheduledTaskAutostartService.ParseTaskXml(
            TaskXml(command: @"C:\Other\app.exe"))!.Value;

        ScheduledTaskAutostartService.Evaluate(d, ExpectedCommand()).Should().Be(AutostartState.Misconfigured);
    }

    [Fact]
    public void Evaluate_MissingBackgroundFlag_IsMisconfigured()
    {
        var d = ScheduledTaskAutostartService.ParseTaskXml(
            TaskXml(command: ExpectedCommand(), arguments: " /normal "))!.Value;

        ScheduledTaskAutostartService.Evaluate(d, ExpectedCommand()).Should().Be(AutostartState.Misconfigured);
    }

    [Fact]
    public void Evaluate_NonLogonTrigger_IsMisconfigured()
    {
        var d = ScheduledTaskAutostartService.ParseTaskXml(
            TaskXml(command: ExpectedCommand(), logonTrigger: false))!.Value;

        ScheduledTaskAutostartService.Evaluate(d, ExpectedCommand()).Should().Be(AutostartState.Misconfigured);
    }

    [Fact]
    public void Evaluate_NonInteractiveLogon_IsMisconfigured()
    {
        var d = ScheduledTaskAutostartService.ParseTaskXml(
            TaskXml(command: ExpectedCommand(), logonType: "Password"))!.Value;

        ScheduledTaskAutostartService.Evaluate(d, ExpectedCommand()).Should().Be(AutostartState.Misconfigured);
    }

    [Fact]
    public void Evaluate_WrongRunLevel_IsMisconfigured()
    {
        var d = ScheduledTaskAutostartService.ParseTaskXml(
            TaskXml(command: ExpectedCommand(), runLevel: "LeastPrivilege"))!.Value;

        ScheduledTaskAutostartService.Evaluate(d, ExpectedCommand()).Should().Be(AutostartState.Misconfigured);
    }

    [Fact]
    public void Evaluate_MissingEnabledElement_WithinSettings_DefaultsToEnabled()
    {
        // [R-AS-XML-VERIFY] <Settings/Enabled> is optional in the Task Scheduler schema and
        // defaults to true when the <Settings> element itself is present: a task without an
        // <Enabled> child must verify as Enabled, not Unknown.
        var xml = TaskXml(command: ExpectedCommand(), enabled: "true")
            .Replace("<Enabled>true</Enabled>", string.Empty, StringComparison.Ordinal);
        var d = ScheduledTaskAutostartService.ParseTaskXml(xml)!.Value;

        var state = ScheduledTaskAutostartService.Evaluate(d, ExpectedCommand());

        state.Should().Be(AutostartState.Enabled);
    }

    [Fact]
    public void Evaluate_MissingSettingsElement_IsMisconfigured_BatteryFlagsUnverifiable()
    {
        // [R-AS-POWER-POLICY] A missing battery power flag is a mismatch (absence is never proof
        // that the task is unrestricted — Windows defaults both flags to true), so a task without
        // any <Settings> block is Misconfigured. The R-AS-XML-VERIFY invariant is preserved: it
        // is never auto-Enabled (its <Settings/Enabled> schema default does not apply here).
        var xml = System.Text.RegularExpressions.Regex.Replace(
            TaskXml(command: ExpectedCommand()),
            @"\s*<Settings>.*?</Settings>\s*",
            string.Empty,
            System.Text.RegularExpressions.RegexOptions.Singleline);
        var d = ScheduledTaskAutostartService.ParseTaskXml(xml)!;

        d.Should().NotBeNull("the task itself is still parsable without a Settings block");
        d!.Value.Enabled.Should().BeNull("no <Settings> element means no verified enabled value");

        ScheduledTaskAutostartService.Evaluate(d.Value, ExpectedCommand())
            .Should().Be(AutostartState.Misconfigured);
        var checks = ScheduledTaskAutostartService.GetMismatches(d.Value, ExpectedCommand())
            .Select(m => m.Check).ToList();
        checks.Should().Contain("DisallowStartIfOnBatteries");
        checks.Should().Contain("StopIfGoingOnBatteries");
    }

    // ------------------------------------------------------------------
    // Real Windows Task Scheduler XML shape + structured mismatch diagnostics
    // ------------------------------------------------------------------

    // A realistic /XML export as schtasks writes it: RegistrationInfo, a full Settings block
    // with IdleSettings, <Actions Context="Author">, a per-trigger Enabled/UserId, and — matching
    // the schtasks round-trip of the quoted /TR executable — a quoted <Command> by default.
    private static string RealTaskXml(
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
    public void ParseTaskXml_RealSchedulerExport_QuotedCommand_IsParsedWithQuotesPreserved()
    {
        const string exe = @"C:\Program Files\ProcessGuardian\ProcessGuardianAdmin.exe";
        var xml = RealTaskXml(command: $"\"{exe}\"");

        var d = ScheduledTaskAutostartService.ParseTaskXml(xml);

        d.Should().NotBeNull();
        d!.Value.Command.Should().Be($"\"{exe}\"", "the raw export value is preserved for diagnosis");
        ScheduledTaskAutostartService.NormalizeCommand(d.Value.Command).Should().Be(exe);
        ScheduledTaskAutostartService.GetMismatches(d.Value, exe).Should().BeEmpty();
        ScheduledTaskAutostartService.Evaluate(d.Value, exe).Should().Be(AutostartState.Enabled);
    }

    [Fact]
    public void Evaluate_RealSchedulerExport_QuotedCommand_IsEnabled_NotMisconfigured()
    {
        const string exe = @"C:\Program Files\ProcessGuardian\ProcessGuardianAdmin.exe";

        var d = ScheduledTaskAutostartService.ParseTaskXml(RealTaskXml(command: $"\"{exe}\""))!.Value;

        ScheduledTaskAutostartService.Evaluate(d, exe).Should().Be(AutostartState.Enabled);
    }

    [Fact]
    public void Evaluate_RealSchedulerExport_UnquotedCommand_IsEnabled()
    {
        var d = ScheduledTaskAutostartService.ParseTaskXml(
            RealTaskXml(command: ExpectedCommand()))!.Value;

        ScheduledTaskAutostartService.Evaluate(d, ExpectedCommand()).Should().Be(AutostartState.Enabled);
    }

    [Fact]
    public void Evaluate_RealSchedulerExport_DisabledFlag_IsDisabled()
    {
        var d = ScheduledTaskAutostartService.ParseTaskXml(
            RealTaskXml(command: ExpectedCommand(), enabled: "false"))!.Value;

        ScheduledTaskAutostartService.Evaluate(d, ExpectedCommand()).Should().Be(AutostartState.Disabled);
    }

    [Theory]
    [InlineData(@"C:\path\app.exe", @"C:\path\app.exe")]
    [InlineData(@"""C:\path\app.exe""", @"C:\path\app.exe")]
    [InlineData(@"  ""C:\path\app.exe""  ", @"C:\path\app.exe")]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("single quote \"", "single quote \"")]
    public void NormalizeCommand_StripsOnlyAPairedOuterQuotePair(string? raw, string? expected)
    {
        ScheduledTaskAutostartService.NormalizeCommand(raw).Should().Be(expected);
    }

    [Fact]
    public void GetMismatches_WrongCommand_ReportsExactCheckExpectedActual()
    {
        const string expected = @"C:\Program Files\ProcessGuardian\ProcessGuardianAdmin.exe";
        const string actual = @"C:\Other\moved.exe";
        var d = ScheduledTaskAutostartService.ParseTaskXml(RealTaskXml(command: actual))!.Value;

        var m = ScheduledTaskAutostartService.GetMismatches(d, expected).Should().ContainSingle().Subject;

        m.Check.Should().Be("Command");
        m.Expected.Should().Be(expected);
        m.Actual.Should().Be(actual);
    }

    [Fact]
    public void GetMismatches_QuotedCommandWithDifferentExe_IsStillACommandMismatch()
    {
        const string expected = @"C:\Program Files\ProcessGuardian\ProcessGuardianAdmin.exe";
        var d = ScheduledTaskAutostartService.ParseTaskXml(
            RealTaskXml(command: @"""C:\Old\ProcessGuardianAdmin.exe"""))!.Value;

        var m = ScheduledTaskAutostartService.GetMismatches(d, expected).Should().ContainSingle().Subject;

        m.Check.Should().Be("Command");
        m.Actual.Should().Contain(@"C:\Old\ProcessGuardianAdmin.exe");
    }

    [Fact]
    public void GetMismatches_MissingCommand_ReportsMissingActual()
    {
        var d = ScheduledTaskAutostartService.ParseTaskXml(RealTaskXml(command: null))!.Value;

        var m = ScheduledTaskAutostartService.GetMismatches(d, ExpectedCommand()).Should().ContainSingle().Subject;

        m.Check.Should().Be("Command");
        m.Actual.Should().Be("<missing>");
    }

    [Fact]
    public void GetMismatches_MissingBackgroundFlag_ReportsExactCheckExpectedActual()
    {
        var d = ScheduledTaskAutostartService.ParseTaskXml(
            RealTaskXml(command: ExpectedCommand(), arguments: "--normal"))!.Value;

        var m = ScheduledTaskAutostartService.GetMismatches(d, ExpectedCommand()).Should().ContainSingle().Subject;

        m.Check.Should().Be("Arguments");
        m.Expected.Should().Be("--background");
        m.Actual.Should().Be("--normal");
    }

    [Fact]
    public void GetMismatches_NoLogonTrigger_ReportsTriggerMismatch()
    {
        var d = ScheduledTaskAutostartService.ParseTaskXml(
            RealTaskXml(command: ExpectedCommand(), logonTrigger: false))!.Value;

        var m = ScheduledTaskAutostartService.GetMismatches(d, ExpectedCommand()).Should().ContainSingle().Subject;

        m.Check.Should().Be("LogonTrigger");
        m.Actual.Should().Be("<none>");
    }

    [Fact]
    public void GetMismatches_WrongLogonType_ReportsExactCheckExpectedActual()
    {
        var d = ScheduledTaskAutostartService.ParseTaskXml(
            RealTaskXml(command: ExpectedCommand(), logonType: "Password"))!.Value;

        var m = ScheduledTaskAutostartService.GetMismatches(d, ExpectedCommand()).Should().ContainSingle().Subject;

        m.Check.Should().Be("LogonType");
        m.Expected.Should().Be("InteractiveToken");
        m.Actual.Should().Be("Password");
    }

    [Fact]
    public void GetMismatches_WrongRunLevel_ReportsExactCheckExpectedActual()
    {
        var d = ScheduledTaskAutostartService.ParseTaskXml(
            RealTaskXml(command: ExpectedCommand(), runLevel: "LeastPrivilege"))!.Value;

        var m = ScheduledTaskAutostartService.GetMismatches(d, ExpectedCommand()).Should().ContainSingle().Subject;

        m.Check.Should().Be("RunLevel");
        m.Expected.Should().Be("HighestAvailable");
        m.Actual.Should().Be("LeastPrivilege");
    }

    [Fact]
    public void GetMismatches_FullyMatchingRealXml_IsEmpty()
    {
        var d = ScheduledTaskAutostartService.ParseTaskXml(
            RealTaskXml(command: $"\"{ExpectedCommand()}\""))!.Value;

        ScheduledTaskAutostartService.GetMismatches(d, ExpectedCommand()).Should().BeEmpty();
    }

    [Fact]
    public void Evaluate_RealSchedulerExport_MissingEnabledElement_DefaultsToEnabled()
    {
        // [R-AS-XML-VERIFY] A real Windows export without <Settings/Enabled> defaults to true
        // (schema default); the previous behavior (Unknown) made a verified task unverifiable.
        var xml = RealTaskXml(command: ExpectedCommand(), enabled: "true")
            .Replace("<Enabled>true</Enabled>", string.Empty, StringComparison.Ordinal);
        var d = ScheduledTaskAutostartService.ParseTaskXml(xml)!;

        var state = ScheduledTaskAutostartService.Evaluate(d.Value, ExpectedCommand());

        state.Should().Be(AutostartState.Enabled);
    }

    // ------------------------------------------------------------------
    // LooksLikeMissingTask
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("ERROR: The system cannot find the file specified.", true)]
    [InlineData("ERROR: Cannot find the file specified.", true)]
    [InlineData("ERROR: Access is denied.", false)]
    [InlineData("ERROR: The service has not been started.", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void LooksLikeMissingTask_OnlyCleanNotFoundMatches(string? output, bool expected)
    {
        ScheduledTaskAutostartService.LooksLikeMissingTask(output).Should().Be(expected);
    }
}
