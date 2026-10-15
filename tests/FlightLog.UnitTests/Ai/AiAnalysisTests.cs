using FlightLog.Application.Ai;
using FlightLog.Application.Messaging;
using FlightLog.Contracts;
using FlightLog.Domain.Analysis;
using FlightLog.Domain.Common;

namespace FlightLog.UnitTests.Ai;

public sealed class AiAnalysisTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);

    private static AiAnalysis NewAnalysis() =>
        AiAnalysis.Queue(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Anthropic", "claude-opus-5-5", FlightReportPrompt.Version, Now);

    private static AiAnalysisInput Input() =>
        new("Anthropic", "claude-opus-5-5", FlightReportPrompt.Version, FlightContextBuilder.Version, new string('a', 64), "{}", new Dictionary<string, string> { ["findings"] = "3" });

    [Fact]
    public void A_queued_analysis_announces_itself_as_a_versioned_message()
    {
        var analysis = NewAnalysis();

        analysis.Status.Should().Be(AiAnalysisStatus.Queued);
        var queued = analysis.DomainEvents.Should().ContainSingle().Which.Should().BeOfType<AiAnalysisQueued>().Subject;
        var message = OutboxMessages.From(queued, Now)!;
        message.Type.Should().Be(MessageTypes.AiAnalyzeFlightV1);
        message.AggregateId.Should().Be(analysis.Id);
        MessageTypes.ClrTypeOf(message.Type).Should().Be<AiAnalyzeFlightV1>();
    }

    [Fact]
    public void Every_call_is_accounted_for_including_failed_ones()
    {
        var analysis = NewAnalysis();
        analysis.Start("w:1", Now);
        analysis.RecordInput(Input());

        analysis.RecordCall(AiUsage.None, 1200, null, null, null);
        analysis.RecordCall(new AiUsage(1000, 500, 200, 0), 8000, 0.0145m, "claude-opus-5-5-20261001", "end_turn");
        analysis.Succeed("{\"summary\":\"x\"}", "{}", Now.AddSeconds(10));

        analysis.Status.Should().Be(AiAnalysisStatus.Succeeded);
        analysis.ProviderCalls.Should().Be(2);
        analysis.LatencyMs.Should().Be(9200);
        analysis.InputTokens.Should().Be(1000);
        analysis.OutputTokens.Should().Be(500);
        analysis.CacheReadTokens.Should().Be(200);
        analysis.CostUsd.Should().Be(0.0145m);
        analysis.ResponseModel.Should().Be("claude-opus-5-5-20261001");
        analysis.StopReason.Should().Be("end_turn");
        analysis.DurationMs.Should().Be(10_000);
        analysis.ContextSummary.Should().Contain("findings", "3");
    }

    [Fact]
    public void Cost_stays_unknown_when_no_call_had_a_price()
    {
        var analysis = NewAnalysis();
        analysis.Start("w:1", Now);

        analysis.RecordCall(new AiUsage(10, 10, 0, 0), 5, null, "some-model", "end_turn");

        analysis.CostUsd.Should().BeNull();
    }

    [Fact]
    public void A_failure_keeps_the_answer_for_audit_and_shortens_the_message()
    {
        var analysis = NewAnalysis();
        analysis.Start("w:1", Now);

        analysis.Fail("ai.invalid_output", new string('x', 5000), "not json", Now.AddSeconds(1));

        analysis.Status.Should().Be(AiAnalysisStatus.Failed);
        analysis.FailureMessage.Should().HaveLength(AiAnalysis.FailureMessageMaxLength);
        analysis.RawOutput.Should().Be("not json");
        analysis.Invoking(a => a.Fail("ai.failed", "again", null, Now)).Should().Throw<DomainRuleViolationException>();
    }

    [Fact]
    public void A_retry_requeues_with_a_due_time_and_announces_again()
    {
        var analysis = NewAnalysis();
        analysis.ClearDomainEvents();
        analysis.Start("w:1", Now);

        analysis.Requeue(Now.AddMinutes(2), countsAsRetry: true);

        analysis.Status.Should().Be(AiAnalysisStatus.Queued);
        analysis.RetryCount.Should().Be(1);
        analysis.NextAttemptAt.Should().Be(Now.AddMinutes(2));
        analysis.DomainEvents.Should().ContainSingle().Which.Should().Be(new AiAnalysisQueued(analysis.Id, analysis.FlightId, analysis.TeamId, Now.AddMinutes(2)));
        analysis.Invoking(a => a.RecordInput(Input())).Should().Throw<DomainRuleViolationException>("input belongs to a running attempt");
    }

    [Fact]
    public void Cost_uses_the_price_list_and_retry_delays_grow()
    {
        var options = new AiOptions { RetryBaseDelay = TimeSpan.FromMinutes(1), RetryMaxDelay = TimeSpan.FromMinutes(3) };

        options.Cost("claude-opus-5-5", new AiUsage(1_000_000, 100_000, 500_000, 0)).Should().Be(4m + 2m + 0.1m);
        options.Cost("CLAUDE-OPUS-5-5", new AiUsage(1000, 0, 0, 0)).Should().Be(0.004m, "model ids are matched case-insensitively");
        options.Cost("unknown-model", new AiUsage(1000, 1000, 0, 0)).Should().BeNull();
        Enumerable.Range(1, 3).Select(options.RetryDelay).Should().Equal(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(3));
    }
}
