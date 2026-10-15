using FlightLog.Application.Ai;
using FlightLog.Domain.Analysis;
using FlightLog.Domain.Common;

namespace FlightLog.Application.Comparison;

/// <summary>
/// Side-by-side comparison of 2 to 6 flights, computed from stored signals, events and the latest rule analysis of each
/// flight. Lists are in the order the flights were requested. Values that cannot be compared directly are flagged
/// rather than hidden.
/// </summary>
public sealed record FlightComparisonDto(
    string Version,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<ComparedFlightDto> Flights,
    IReadOnlyList<ComparedSignalDto> Signals,
    IReadOnlyList<ComparedRuleDto> Rules,
    IReadOnlyList<ComparabilityNoteDto> Notes,
    IReadOnlyList<string> Limitations);

public sealed record ComparedFlightDto(
    Guid Id,
    string Name,
    string ProjectName,
    string? AircraftName,
    Autopilot Autopilot,
    string? FirmwareVersion,
    string? VehicleType,
    string? Hardware,
    DateTimeOffset? StartedAt,
    double? DurationSeconds,
    double? AnalyzedSeconds,
    bool ArmingKnown,
    Guid? AnalysisRunId,
    string? EngineVersion,
    int? InfoCount,
    int? WarningCount,
    int? CriticalCount,
    IReadOnlyDictionary<string, int> FindingsByCategory,
    IReadOnlyDictionary<string, int> EventCounts,
    IReadOnlyList<ModeShareDto> Modes,
    BatterySummaryDto? Battery,
    VibrationSummaryDto? Vibration,
    AiReportRefDto? LatestAiReport);

/// <summary>Time spent in a flight mode during the analyzed time (armed periods, or the whole log without arming information).</summary>
public sealed record ModeShareDto(string Mode, double Seconds, double Share);

public sealed record BatterySummaryDto(
    double? StartVoltage,
    double? EndVoltage,
    double? MinVoltage,
    double? MeanCurrent,
    double? MaxCurrent,
    double? StartRemaining,
    double? EndRemaining,
    double? UsedPercent);

/// <summary>
/// Vibration in the autopilot's own metric. ArduPilot (VIBE, m/s/s) and PX4 (accel_vibration_metric) measure different
/// things, so values are only comparable between flights of the same autopilot.
/// </summary>
public sealed record VibrationSummaryDto(string Metric, string? Unit, Autopilot ComparableWithin, double Mean, double P95, double Max);

public sealed record AiReportRefDto(Guid Id, DateTimeOffset? FinishedAt, string Model, AiOverallAssessment? Assessment, string? Summary);

public sealed record SignalStatsDto(long Samples, double RateHz, double Min, double Max, double Mean, double P5, double P50, double P95);

/// <summary>One normalized signal across the flights; a null value means the flight has no valid samples of it.</summary>
public sealed record ComparedSignalDto(string Name, string? Unit, bool Comparable, string? Note, IReadOnlyList<SignalStatsDto?> Values);

/// <summary>A rule across the flights: findings and highest severity per flight; null where the flight has no rule analysis.</summary>
public sealed record ComparedRuleDto(string RuleId, IReadOnlyList<ComparedRuleResultDto?> Values);

public sealed record ComparedRuleResultDto(RuleResultStatus Status, int Findings, FindingSeverity? HighestSeverity);

/// <summary>Why some values cannot be compared one to one. <c>FlightIds</c> empty means it concerns all flights.</summary>
public sealed record ComparabilityNoteDto(string Code, string Message, IReadOnlyList<Guid> FlightIds);

public sealed record CompareFlightsRequest(IReadOnlyList<Guid> FlightIds)
{
    public const int MinFlights = 2;
    public const int MaxFlights = 6;
}
