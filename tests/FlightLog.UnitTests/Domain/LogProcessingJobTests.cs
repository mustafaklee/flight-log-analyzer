using FlightLog.Domain.Common;
using FlightLog.Domain.FlightData;

namespace FlightLog.UnitTests.Domain;

public sealed class LogProcessingJobTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 8, 0, 0, TimeSpan.Zero);

    private static LogProcessingJob NewJob() =>
        LogProcessingJob.Start(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, "host:1", "corr", Now);

    [Fact]
    public void A_new_job_runs_and_writes_under_its_own_prefix()
    {
        var job = NewJob();

        job.Outcome.Should().Be(ProcessingJobOutcome.Running);
        job.StoragePrefix.Should().Be(LogProcessingJob.StoragePrefixFor(job.FlightId, job.Id));
        job.StoragePrefix.Should().StartWith($"derived/{job.FlightId:N}/").And.EndWith("/");
    }

    [Fact]
    public void Success_records_statistics_duration_and_bounded_warnings()
    {
        var job = NewJob();
        var warnings = Enumerable.Range(0, 150).Select(i => i == 0 ? new string('w', 900) : $"warning {i}").ToList();

        job.Succeed(new ProcessingStatistics { ParserName = "ulog", RecordCount = 42, SignalCount = 7, Truncated = true, Warnings = warnings }, 1024, Now.AddSeconds(3));

        job.Outcome.Should().Be(ProcessingJobOutcome.Succeeded);
        job.DurationMs.Should().Be(3000);
        job.AllocatedBytes.Should().Be(1024);
        job.RecordCount.Should().Be(42);
        job.Truncated.Should().BeTrue();
        job.Warnings.Should().HaveCount(LogProcessingJob.MaxWarnings);
        job.Warnings[0].Should().HaveLength(500);
    }

    [Fact]
    public void A_finished_job_cannot_end_again()
    {
        var job = NewJob();
        job.Fail("ulog.invalid_header", "Not a ULog file.", 0, Now);

        var act = () => job.Cancel(0, Now);

        act.Should().Throw<DomainRuleViolationException>().Which.Code.Should().Be("processing_job.already_finished");
        job.FailureCode.Should().Be("ulog.invalid_header");
    }

    [Fact]
    public void Attempts_start_at_one()
    {
        var act = () => LogProcessingJob.Start(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 0, "w", "c", Now);

        act.Should().Throw<DomainRuleViolationException>();
    }
}
