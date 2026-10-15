using FlightLog.Domain.Analysis;

namespace FlightLog.Application.Analysis;

/// <summary>
/// What a rule checks, which data it needs and when it does not apply. Served by the API so that users can see
/// exactly how a finding came about; the text is part of the rule's version.
/// </summary>
public sealed record RuleDescriptor(
    string Id,
    int Version,
    string Category,
    string Title,
    string Description,
    IReadOnlyList<string> RequiredData,
    string Applicability);

/// <summary>A finding before it is stored with its run.</summary>
public sealed record FindingDraft
{
    public required string Title { get; init; }

    public required string Description { get; init; }

    public required FindingSeverity Severity { get; init; }

    public required FindingConfidence Confidence { get; init; }

    public required long StartUs { get; init; }

    public required long EndUs { get; init; }

    public required IReadOnlyList<string> Signals { get; init; }

    public IReadOnlyDictionary<string, double> Measured { get; init; } = new Dictionary<string, double>();

    public IReadOnlyDictionary<string, double> Threshold { get; init; } = new Dictionary<string, double>();

    public required string ThresholdSource { get; init; }

    public double? DataCoverage { get; init; }

    public int OccurrenceCount { get; init; } = 1;

    public string? Limitations { get; init; }
}

/// <summary>The result of one rule: findings (possibly none), or why the rule did not apply.</summary>
public sealed record RuleEvaluation(RuleResultStatus Status, string? Reason, IReadOnlyList<FindingDraft> Findings, IReadOnlyList<string> Signals)
{
    public static RuleEvaluation NotApplicable(string reason) => new(RuleResultStatus.NotApplicable, reason, [], []);

    public static RuleEvaluation Applied(IReadOnlyList<FindingDraft> findings, IReadOnlyList<string> signals) =>
        new(RuleResultStatus.Applied, null, findings, signals);
}

/// <summary>
/// A deterministic technical check. Rules are not universal diagnoses: each one states the data it needs and
/// returns <see cref="RuleEvaluation.NotApplicable"/> instead of guessing when the log does not have it.
/// </summary>
public interface IAnalysisRule
{
    RuleDescriptor Descriptor { get; }

    Task<RuleEvaluation> EvaluateAsync(AnalysisContext context, CancellationToken cancellationToken);
}
