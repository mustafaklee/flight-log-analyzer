using FlightLog.Domain.Common;
using FlightLog.Domain.Flights;

namespace FlightLog.UnitTests.Domain;

public sealed class FlightLogFileTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly string Hash = new('a', 64);

    private static FlightLogFile NewFile(long size = 100, string? sha = null) =>
        FlightLogFile.Create(Guid.NewGuid(), Guid.NewGuid(), "flight.ulg", size, sha ?? Hash, LogFormat.Px4ULog, "raw/" + Hash, Guid.NewGuid(), Now);

    private static FlightLogFile QueuedFile()
    {
        var file = NewFile();
        file.TransitionTo(ProcessingStatus.Validating, Now);
        file.TransitionTo(ProcessingStatus.Queued, Now);
        return file;
    }

    [Fact]
    public void A_new_file_starts_as_uploaded()
    {
        var file = NewFile();

        file.Status.Should().Be(ProcessingStatus.Uploaded);
        file.StatusChangedAt.Should().Be(Now);
        file.FailureCode.Should().BeNull();
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void Hash_must_be_lower_case_hex_sha256(string sha)
    {
        var act = () => NewFile(sha: sha);

        act.Should().Throw<DomainRuleViolationException>().Which.Code.Should().Be("log_file.invalid_hash");
    }

    [Fact]
    public void Empty_files_are_rejected()
    {
        var act = () => NewFile(size: 0);

        act.Should().Throw<DomainRuleViolationException>().Which.Code.Should().Be("log_file.empty");
    }

    [Fact]
    public void Failure_needs_a_reason_and_is_cleared_on_retry()
    {
        var file = QueuedFile();
        file.TransitionTo(ProcessingStatus.Processing, Now);

        var plainFail = () => file.TransitionTo(ProcessingStatus.Failed, Now);
        plainFail.Should().Throw<DomainRuleViolationException>().Which.Code.Should().Be("log_file.failure_requires_reason");

        file.Fail("parser.truncated", new string('x', 5000), Now.AddMinutes(1));
        file.Status.Should().Be(ProcessingStatus.Failed);
        file.FailureCode.Should().Be("parser.truncated");
        file.FailureMessage.Should().HaveLength(FlightLogFile.FailureMessageMaxLength);

        file.Retry(Now.AddMinutes(2));
        file.Status.Should().Be(ProcessingStatus.Queued);
        file.FailureCode.Should().BeNull();
        file.FailureMessage.Should().BeNull();
        file.StatusChangedAt.Should().Be(Now.AddMinutes(2));
    }

    [Fact]
    public void Only_failed_or_cancelled_files_can_be_retried()
    {
        var file = QueuedFile();

        var act = () => file.Retry(Now);

        act.Should().Throw<DomainRuleViolationException>().Which.Code.Should().Be("log_file.not_retryable");
    }

    [Fact]
    public void Cancel_is_allowed_while_queued_but_not_after_completion()
    {
        var file = QueuedFile();
        file.Cancel(Now);
        file.Status.Should().Be(ProcessingStatus.Cancelled);

        var again = () => file.Cancel(Now);
        again.Should().Throw<DomainRuleViolationException>().Which.Code.Should().Be("log_file.not_cancellable");
    }

    [Fact]
    public void Entering_the_queue_raises_an_event_for_the_outbox()
    {
        var file = QueuedFile();

        file.DomainEvents.Should().ContainSingle().Which.Should().Be(new LogFileQueued(file.Id, file.TeamId, null));
        file.ClearDomainEvents();
        file.DomainEvents.Should().BeEmpty();
    }

    [Fact]
    public void An_automatic_retry_counts_waits_and_announces_its_due_time()
    {
        var file = QueuedFile();
        file.TransitionTo(ProcessingStatus.Processing, Now);
        file.ClearDomainEvents();
        var due = Now.AddSeconds(30);

        file.ScheduleRetry(due, Now);

        file.Status.Should().Be(ProcessingStatus.Queued);
        file.RetryCount.Should().Be(1);
        file.NextAttemptAt.Should().Be(due);
        file.DomainEvents.Should().ContainSingle().Which.Should().Be(new LogFileQueued(file.Id, file.TeamId, due));
    }

    [Fact]
    public void Only_a_file_in_processing_can_be_retried_automatically()
    {
        var act = () => QueuedFile().ScheduleRetry(Now, Now);

        act.Should().Throw<DomainRuleViolationException>().Which.Code.Should().Be("log_file.not_processing");
    }

    [Fact]
    public void A_user_retry_resets_the_automatic_retry_count()
    {
        var file = QueuedFile();
        file.TransitionTo(ProcessingStatus.Processing, Now);
        file.ScheduleRetry(Now.AddMinutes(1), Now);
        file.TransitionTo(ProcessingStatus.Processing, Now);
        file.Fail("processing.retries_exhausted", "gave up", Now);

        file.Retry(Now);

        file.RetryCount.Should().Be(0);
        file.NextAttemptAt.Should().BeNull();
        file.FailureCode.Should().BeNull();
    }

    [Fact]
    public void Analysis_can_be_requested_after_processing_completion_or_a_failed_analysis_only()
    {
        var file = QueuedFile();
        file.TransitionTo(ProcessingStatus.Processing, Now);
        file.TransitionTo(ProcessingStatus.Processed, Now);
        file.RequestAnalysis(Now);
        file.Status.Should().Be(ProcessingStatus.AnalysisPending);

        file.TransitionTo(ProcessingStatus.Analyzing, Now);
        file.Fail("analysis.failed", "boom", Now);
        file.CanRequestAnalysis.Should().BeTrue("the processed data is intact after an analysis failure");
        file.RequestAnalysis(Now);
        file.FailureCode.Should().BeNull();

        var failedProcessing = QueuedFile();
        failedProcessing.TransitionTo(ProcessingStatus.Processing, Now);
        failedProcessing.Fail("ulog.invalid_header", "bad", Now);
        failedProcessing.CanRequestAnalysis.Should().BeFalse();
        var act = () => failedProcessing.RequestAnalysis(Now);
        act.Should().Throw<DomainRuleViolationException>().Which.Code.Should().Be("log_file.not_analyzable");
    }
}
