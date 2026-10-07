using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ProcessGuardian.Domain.Enums;
using ProcessGuardian.Infrastructure.Status;
using Xunit;

namespace ProcessGuardian.Infrastructure.Tests;

public class StatusReporterTests
{
    [Fact]
    public void Report_SubscriberThrows_ReporterSurvives_CurrentStillUpdated()
    {
        var reporter = new StatusReporter(NullLogger<StatusReporter>.Instance);
        reporter.StatusChanged += (_, _) => throw new InvalidOperationException("boom");

        var act = () => reporter.Report(GuardianStatus.Monitoring);

        act.Should().NotThrow("a broken subscriber must not break the reporter");
        reporter.Current.Should().Be(GuardianStatus.Monitoring, "the state is set before the event fires");
    }

    [Fact]
    public void Report_NoLogger_ThrowsStillSwallowed()
    {
        var reporter = new StatusReporter();
        reporter.StatusChanged += (_, _) => throw new InvalidOperationException("boom");

        var act = () => reporter.Report(GuardianStatus.Error);

        act.Should().NotThrow();
        reporter.Current.Should().Be(GuardianStatus.Error);
    }
}
