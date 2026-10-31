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

    [Fact]
    public void BuildCreateArguments_CreatesOnLogonTaskWithHighestRunLevel()
    {
        var args = ScheduledTaskAutostartService.BuildCreateArguments(@"C:\Tools\app.exe");

        args.Should().Be(
            $"/Create /TN \"{AppEdition.ScheduledTaskName}\" /TR \"\\\"C:\\Tools\\app.exe\\\" --background\" /SC ONLOGON /RL HIGHEST /F");
    }

    [Fact]
    public void BuildCreateArguments_QuotesTaskTrigger_WithBackgroundFlag()
    {
        var args = ScheduledTaskAutostartService.BuildCreateArguments(@"C:\My Tools\app.exe");

        args.Should().Contain("/TR \"\\\"C:\\My Tools\\app.exe\\\" --background\"");
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
    public void Evaluate_MissingEnabledFlag_IsUnknown_NotEnabled()
    {
        var xml = TaskXml(command: ExpectedCommand(), enabled: "true")
            .Replace("<Enabled>true</Enabled>", string.Empty, StringComparison.Ordinal);
        var d = ScheduledTaskAutostartService.ParseTaskXml(xml)!.Value;

        var state = ScheduledTaskAutostartService.Evaluate(d, ExpectedCommand());

        state.Should().Be(AutostartState.Unknown);
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
