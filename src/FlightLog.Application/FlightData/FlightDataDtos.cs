using FlightLog.Domain.FlightData;

namespace FlightLog.Application.FlightData;

/// <summary>Filters for the signal catalog of a flight.</summary>
public sealed record SignalQuery(SignalKind? Kind = null, string? Search = null);

/// <summary>Where a normalized signal comes from.</summary>
public sealed record SignalSourceDto(string Message, int Instance, string Fields, string Conversion, string NormalizationVersion);

/// <summary>
/// A signal of a flight. Values are in <see cref="Unit"/> (as the log declares it); a multiplier the log declares
/// is already applied to <see cref="Min"/>, <see cref="Max"/> and to the data. Times are µs on the log clock.
/// </summary>
public sealed record SignalDto(
    string Name,
    SignalKind Kind,
    string Message,
    int Instance,
    string Field,
    string? Unit,
    double? Multiplier,
    string SourceType,
    SignalSourceDto? Source,
    long SampleCount,
    long InvalidCount,
    double? Min,
    double? Max,
    long? FirstTimestampUs,
    long? LastTimestampUs);

/// <summary>Request for signal values. Without a time range the whole flight is returned.</summary>
public sealed record SignalDataQuery(IReadOnlyList<string> Names, long? FromUs = null, long? ToUs = null, int MaxPoints = SignalDataQuery.DefaultMaxPoints)
{
    public const int DefaultMaxPoints = 2000;
    public const int MaxMaxPoints = 20_000;
    public const int MaxSignals = 8;
}

/// <summary>
/// Values of one signal. Invalid samples (NaN or infinite in the log) are null. When the range holds more samples
/// than requested, each time bucket keeps its minimum and maximum so peaks stay visible (<see cref="Downsampled"/>).
/// </summary>
public sealed record SignalSeriesDataDto(
    string Name,
    string? Unit,
    long SamplesInRange,
    bool Downsampled,
    IReadOnlyList<long> TimestampsUs,
    IReadOnlyList<double?> Values);

public sealed record SignalDataDto(Guid FlightId, long? LogStartTimestampUs, DateTimeOffset? BootTimeUtc, IReadOnlyList<SignalSeriesDataDto> Signals);

public sealed record EventQuery(
    FlightEventKind? Kind = null,
    EventSeverity? MaxSeverity = null,
    long? FromUs = null,
    long? ToUs = null,
    string? Search = null,
    int Page = 1,
    int PageSize = 100)
{
    public const int MaxPageSize = 1000;
}

public sealed record FlightEventDto(long TimestampUs, FlightEventKind Kind, EventSeverity? Severity, string Source, string Text, IReadOnlyDictionary<string, string>? Data);

public sealed record ParameterQuery(string? Search = null, bool ChangedOnly = false);

public sealed record ParameterChangeDto(long TimestampUs, double Value);

/// <summary>A parameter: its value at log start, the firmware default when the log has it, and changes in flight.</summary>
public sealed record FlightParameterDto(string Name, double? InitialValue, double? DefaultValue, IReadOnlyList<ParameterChangeDto> Changes);

public sealed record ProcessingJobDto(
    Guid Id,
    int Attempt,
    ProcessingJobOutcome Outcome,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    long? DurationMs,
    long? AllocatedBytes,
    string? ParserName,
    string? ParserVersion,
    string? NormalizationVersion,
    long MessageCount,
    long RecordCount,
    int SeriesCount,
    int SignalCount,
    int EventCount,
    int ParameterCount,
    int ChunkCount,
    long ChunkBytes,
    long SkippedBytes,
    bool Truncated,
    bool Corrupt,
    bool SummaryTruncated,
    IReadOnlyList<string> Warnings,
    string? FailureCode,
    string? FailureMessage,
    string CorrelationId);
