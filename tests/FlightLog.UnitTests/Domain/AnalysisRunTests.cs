using FlightLog.Domain.Analysis;
using FlightLog.Domain.Common;

namespace FlightLog.UnitTests.Domain;

public sealed class AnalysisRunTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);

    private static AnalysisRun NewRun() => AnalysisRun.Queue(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), AnalysisTrigger.Automatic, null, Now);

    [Fact]
    public void A_queued_run_announces_itself()
    {
        var run = NewRun();

        run.Status.Should().Be(AnalysisRunStatus.Queued);
        run.DomainEvents.Should().ContainSingle().Which.Should().Be(new AnalysisRunQueued(run.Id, run.FlightId, run.TeamId, null));
    }

    [Fact]
    public void A_manual_run_needs_its_requester()
    {
        var act = () => AnalysisRun.Queue(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), AnalysisTrigger.Manual, null, Now);

        act.Should().Throw<DomainRuleViolationException>().Which.Code.Should().Be("analysis_run.requester_required");
    }

    [Fact]
    public void Success_records_engine_version_counts_input_and_duration()
    {
        var run = NewRun();
        run.Start("w:1", "rules-1", Now);

        run.Succeed(new AnalysisCounts(2, 1, 1, 10, 2, 1), new Dictionary<string, string> { ["armedPeriods"] = "1" }, Now.AddSeconds(2));

        run.Status.Should().Be(AnalysisRunStatus.Succeeded);
        run.EngineVersion.Should().Be("rules-1");
        run.Attempt.Should().Be(1);
        run.FindingCount.Should().Be(4);
        run.CriticalCount.Should().Be(1);
        run.RulesFailed.Should().Be(1);
        run.DurationMs.Should().Be(2000);
        run.InputSummary.Should().Contain("armedPeriods", "1");
    }

    [Fact]
    public void A_requeued_run_counts_retries_only_for_failures_and_can_start_again()
    {
        var run = NewRun();
        run.Start("w:1", "rules-1", Now);
        run.ClearDomainEvents();

        run.Requeue(null, countsAsRetry: false);
        run.RetryCount.Should().Be(0);
        run.Start("w:2", "rules-1", Now);
        run.Requeue(Now.AddMinutes(1), countsAsRetry: true);

        run.Status.Should().Be(AnalysisRunStatus.Queued);
        run.RetryCount.Should().Be(1);
        run.NextAttemptAt.Should().Be(Now.AddMinutes(1));
        run.DomainEvents.Should().HaveCount(2).And.ContainEquivalentOf(new AnalysisRunQueued(run.Id, run.FlightId, run.TeamId, Now.AddMinutes(1)));
    }

    [Fact]
    public void A_finished_run_cannot_change_again()
    {
        var run = NewRun();
        run.Fail("analysis.superseded", "replaced", Now);

        var start = () => run.Start("w", "rules-1", Now);
        var fail = () => run.Fail("x", "y", Now);

        start.Should().Throw<DomainRuleViolationException>().Which.Code.Should().Be("analysis_run.invalid_status");
        fail.Should().Throw<DomainRuleViolationException>().Which.Code.Should().Be("analysis_run.already_finished");
        run.DurationMs.Should().BeNull("it never started");
    }
}
