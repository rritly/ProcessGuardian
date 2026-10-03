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
}
