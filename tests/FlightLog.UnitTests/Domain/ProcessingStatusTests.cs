using FlightLog.Domain.Common;
using FlightLog.Domain.Flights;

namespace FlightLog.UnitTests.Domain;

public sealed class ProcessingStatusTests
{
    private static readonly (ProcessingStatus From, ProcessingStatus To)[] Allowed =
    [
        (ProcessingStatus.Uploaded, ProcessingStatus.Validating),
        (ProcessingStatus.Validating, ProcessingStatus.Queued),
        (ProcessingStatus.Validating, ProcessingStatus.Failed),
        (ProcessingStatus.Queued, ProcessingStatus.Processing),
        (ProcessingStatus.Queued, ProcessingStatus.Cancelled),
        (ProcessingStatus.Processing, ProcessingStatus.Processed),
        (ProcessingStatus.Processing, ProcessingStatus.Failed),
        (ProcessingStatus.Processing, ProcessingStatus.Cancelled),
        (ProcessingStatus.Processing, ProcessingStatus.Queued),
        (ProcessingStatus.Processed, ProcessingStatus.AnalysisPending),
        (ProcessingStatus.AnalysisPending, ProcessingStatus.Analyzing),
        (ProcessingStatus.AnalysisPending, ProcessingStatus.Cancelled),
        (ProcessingStatus.Analyzing, ProcessingStatus.Completed),
        (ProcessingStatus.Analyzing, ProcessingStatus.Failed),
        (ProcessingStatus.Analyzing, ProcessingStatus.AnalysisPending),
        (ProcessingStatus.Completed, ProcessingStatus.AnalysisPending),
        (ProcessingStatus.Failed, ProcessingStatus.Queued),
        (ProcessingStatus.Failed, ProcessingStatus.AnalysisPending),
        (ProcessingStatus.Cancelled, ProcessingStatus.Queued),
    ];

    [Fact]
    public void Exactly_the_documented_transitions_are_allowed()
    {
        var all = Enum.GetValues<ProcessingStatus>();
        foreach (var from in all)
        {
            foreach (var to in all)
            {
                var expected = Allowed.Contains((from, to));
                ProcessingStatusTransitions.CanTransition(from, to).Should().Be(expected, $"{from} -> {to}");
            }
        }
    }

    [Fact]
    public void Rejected_transitions_carry_a_stable_code()
    {
        var act = () => ProcessingStatusTransitions.EnsureCanTransition(ProcessingStatus.Uploaded, ProcessingStatus.Completed);

        act.Should().Throw<DomainRuleViolationException>().Which.Code.Should().Be("log_file.invalid_status_transition");
    }

    [Theory]
    [InlineData(ProcessingStatus.Completed, true)]
    [InlineData(ProcessingStatus.Failed, true)]
    [InlineData(ProcessingStatus.Cancelled, true)]
    [InlineData(ProcessingStatus.Queued, false)]
    [InlineData(ProcessingStatus.Processing, false)]
    public void Terminal_states(ProcessingStatus status, bool expected) =>
        ProcessingStatusTransitions.IsTerminal(status).Should().Be(expected);
}
