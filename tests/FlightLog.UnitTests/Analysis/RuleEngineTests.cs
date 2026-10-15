using FlightLog.Application.Abstractions;
using FlightLog.Application.Analysis;
using FlightLog.Domain.Analysis;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FlightLog.UnitTests.Analysis;

public sealed class RuleEngineTests
{
    private static RuleDescriptor Descriptor(string id) => new(id, 3, "test", id, "d", [], "a");

    private sealed class FixedRule(string id, Func<RuleEvaluation> evaluate) : IAnalysisRule
    {
        public RuleDescriptor Descriptor { get; } = RuleEngineTests.Descriptor(id);

        public Task<RuleEvaluation> EvaluateAsync(AnalysisContext context, CancellationToken cancellationToken) => Task.FromResult(evaluate());
    }

    private static FindingDraft Draft(long start, FindingSeverity severity = FindingSeverity.Warning) => new()
    {
        Title = new string('t', 300),
        Description = "d",
        Severity = severity,
        Confidence = FindingConfidence.Medium,
        StartUs = start,
        EndUs = start - 5,
        Signals = ["a"],
        Measured = new Dictionary<string, double> { ["ok"] = 1, ["nan"] = double.NaN },
        ThresholdSource = "test",
    };

    private static RuleEngine Engine(ITransientErrorClassifier? errors, params IAnalysisRule[] rules) =>
        new(rules, errors ?? Substitute.For<ITransientErrorClassifier>(), NullLogger<RuleEngine>.Instance);

    [Fact]
    public async Task A_failing_rule_is_recorded_and_the_others_still_run()
    {
        var engine = Engine(
            null,
            new FixedRule("b.ok", () => RuleEvaluation.Applied([Draft(10, FindingSeverity.Critical), Draft(5)], ["x"])),
            new FixedRule("a.broken", () => throw new InvalidOperationException("bug")),
            new FixedRule("c.na", () => RuleEvaluation.NotApplicable("no data")));

        var result = await engine.RunAsync(new RuleTestContext().Build(), Guid.NewGuid(), Guid.NewGuid(), TestContext.Current.CancellationToken);

        result.RuleResults.Select(r => (r.RuleId, r.Status)).Should().Equal(
            ("a.broken", RuleResultStatus.Failed), ("b.ok", RuleResultStatus.Applied), ("c.na", RuleResultStatus.NotApplicable));
        result.RuleResults[0].Reason.Should().Contain("InvalidOperationException").And.NotContain("bug");
        result.Counts.Should().Be(new AnalysisCounts(0, 1, 1, 1, 1, 1));

        var first = result.Findings[0];
        first.StartUs.Should().Be(5, "findings are stored in time order");
        first.EndUs.Should().Be(5, "an end before the start is clamped");
        first.Title.Should().HaveLength(AnalysisFinding.TitleMaxLength);
        first.Measured.Should().ContainKey("ok").And.NotContainKey("nan");
        first.RuleVersion.Should().Be(3);
        first.Category.Should().Be("test");
    }

    [Fact]
    public async Task Transient_failures_abort_the_run_so_that_it_is_retried()
    {
        var errors = Substitute.For<ITransientErrorClassifier>();
        errors.IsTransient(Arg.Any<Exception>()).Returns(true);
        var engine = Engine(errors, new FixedRule("a", () => throw new TimeoutException()));

        var act = () => engine.RunAsync(new RuleTestContext().Build(), Guid.NewGuid(), Guid.NewGuid(), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<TimeoutException>();
    }

    [Fact]
    public async Task Findings_per_rule_are_capped()
    {
        var engine = Engine(null, new FixedRule("noisy", () => RuleEvaluation.Applied([.. Enumerable.Range(0, 250).Select(i => Draft(i))], [])));

        var result = await engine.RunAsync(new RuleTestContext().Build(), Guid.NewGuid(), Guid.NewGuid(), TestContext.Current.CancellationToken);

        result.Findings.Should().HaveCount(RuleEngine.MaxFindingsPerRule);
        result.RuleResults.Single().Reason.Should().Contain("200 of 250");
    }
}
