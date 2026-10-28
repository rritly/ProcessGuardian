using System.Diagnostics;
using System.IO;
using FluentAssertions;
using ProcessGuardian.Domain;
using ProcessGuardian.Infrastructure.Autostart;
using Xunit;

namespace ProcessGuardian.Infrastructure.Tests;

// Command-line construction for schtasks.exe (admin edition autostart).
public class ScheduledTaskAutostartServiceTests
{
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
}
