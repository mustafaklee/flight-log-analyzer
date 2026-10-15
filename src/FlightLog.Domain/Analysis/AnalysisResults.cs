namespace FlightLog.Domain.Analysis;

// What a run found. Written once, together with the run's success, and never changed afterwards (review state
// and comments arrive in stage 7 as separate records).

public enum FindingSeverity
{
    Info = 1,
    Warning = 2,
    Critical = 3,
}

/// <summary>
/// How much a finding can be trusted. High: the autopilot itself reported it (event, failsafe). Medium: a
/// measured value crossed a threshold that the vehicle's own parameters or the autopilot documentation define,
/// with good data coverage. Low: a heuristic threshold or incomplete data.
/// </summary>
public enum FindingConfidence
{
    Low = 1,
    Medium = 2,
    High = 3,
}

public enum RuleResultStatus
{
    /// <summary>The rule ran on the data it needs (it may have found nothing).</summary>
    Applied = 1,

    /// <summary>The log lacks what the rule needs, or the rule does not apply to this vehicle; see the reason.</summary>
    NotApplicable = 2,

    /// <summary>The rule threw; the other rules of the run are unaffected.</summary>
    Failed = 3,
}

/// <summary>A finding of one rule: a time range of the flight, what was measured and why it is reported.</summary>
public sealed record AnalysisFinding
{
    public const int TitleMaxLength = 200;
    public const int DescriptionMaxLength = 2000;

    public required Guid Id { get; init; }

    public required Guid RunId { get; init; }

    public required Guid FlightId { get; init; }

    public required string RuleId { get; init; }

    public required int RuleVersion { get; init; }

    /// <summary>gps, ekf, vibration, battery, events, modes, data, parameters.</summary>
    public required string Category { get; init; }

    public required string Title { get; init; }

    public required string Description { get; init; }

    public required FindingSeverity Severity { get; init; }

    public required FindingConfidence Confidence { get; init; }

    /// <summary>Log time (µs, same clock as signals and events).</summary>
    public required long StartUs { get; init; }

    public required long EndUs { get; init; }

    /// <summary>Signal names (as the signals API lists them) and event sources the finding is based on.</summary>
    public required IReadOnlyList<string> Signals { get; init; }

    /// <summary>Measured values, for example <c>maxVibeX</c> or <c>durationS</c>.</summary>
    public required IReadOnlyDictionary<string, double> Measured { get; init; }

    /// <summary>The thresholds applied, for example <c>vibration</c> = 30.</summary>
    public required IReadOnlyDictionary<string, double> Threshold { get; init; }

    /// <summary>Where the thresholds come from: a vehicle parameter, autopilot documentation or a heuristic.</summary>
    public required string ThresholdSource { get; init; }

    /// <summary>Share (0..1) of the analyzed time that the rule's signals actually cover; null for event rules.</summary>
    public double? DataCoverage { get; init; }

    /// <summary>Raw occurrences merged into this finding (repeated events, adjacent intervals).</summary>
    public required int OccurrenceCount { get; init; }

    /// <summary>What the finding cannot tell, in plain words.</summary>
    public string? Limitations { get; init; }
}

/// <summary>How one rule went in a run: applied, not applicable (and why) or failed.</summary>
public sealed record AnalysisRuleResult
{
    public required Guid RunId { get; init; }

    public required string RuleId { get; init; }

    public required int RuleVersion { get; init; }

    public required RuleResultStatus Status { get; init; }

    /// <summary>Why the rule did not apply or failed; null when it applied.</summary>
    public string? Reason { get; init; }

    public required int FindingCount { get; init; }

    /// <summary>The signals the rule read.</summary>
    public required IReadOnlyList<string> Signals { get; init; }

    public required long DurationMs { get; init; }
}
