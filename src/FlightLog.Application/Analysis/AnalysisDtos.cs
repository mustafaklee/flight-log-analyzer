using FlightLog.Domain.Analysis;

namespace FlightLog.Application.Analysis;

/// <summary>The latest successful analysis of a flight, for lists and the flight page.</summary>
public sealed record AnalysisSummaryDto(Guid RunId, DateTimeOffset? FinishedAt, int FindingCount, int InfoCount, int WarningCount, int CriticalCount)
{
    /// <summary>Highest severity found, null without findings.</summary>
    public FindingSeverity? HighestSeverity => CriticalCount > 0 ? FindingSeverity.Critical
        : WarningCount > 0 ? FindingSeverity.Warning
        : InfoCount > 0 ? FindingSeverity.Info : null;
}

public sealed record AnalysisRunDto(
    Guid Id,
    Guid FlightId,
    Guid LogFileId,
    AnalysisKind Kind,
    AnalysisTrigger Trigger,
    AnalysisRunStatus Status,
    string EngineVersion,
    Guid? RequestedBy,
    int Attempt,
    int RetryCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? NextAttemptAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    long? DurationMs,
    int FindingCount,
    int InfoCount,
    int WarningCount,
    int CriticalCount,
    int RulesApplied,
    int RulesNotApplicable,
    int RulesFailed,
    IReadOnlyDictionary<string, string> InputSummary,
    string? FailureCode,
    string? FailureMessage,
    string CorrelationId,
    IReadOnlyList<RuleResultDto>? Rules);

public sealed record RuleResultDto(string RuleId, int RuleVersion, RuleResultStatus Status, string? Reason, int FindingCount, IReadOnlyList<string> Signals, long DurationMs);

public sealed record FindingDto(
    Guid Id,
    Guid RunId,
    string RuleId,
    int RuleVersion,
    string Category,
    string Title,
    string Description,
    FindingSeverity Severity,
    FindingConfidence Confidence,
    long StartUs,
    long EndUs,
    IReadOnlyList<string> Signals,
    IReadOnlyDictionary<string, double> Measured,
    IReadOnlyDictionary<string, double> Threshold,
    string ThresholdSource,
    double? DataCoverage,
    int OccurrenceCount,
    string? Limitations);

public sealed record FindingQuery(FindingSeverity? MinSeverity = null, string? Category = null, string? RuleId = null, int Page = 1, int PageSize = 50)
{
    public const int MaxPageSize = 200;
}

public sealed record RuleDescriptorDto(string Id, int Version, string Category, string Title, string Description, IReadOnlyList<string> RequiredData, string Applicability);
