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
}
