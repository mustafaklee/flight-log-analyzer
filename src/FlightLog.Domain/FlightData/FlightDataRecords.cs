namespace FlightLog.Domain.FlightData;

// Facts derived from a log file. They never change after a processing run has written them; reprocessing
// replaces them as a whole. They are therefore immutable records (init-only) rather than entities with behaviour.

public enum SignalKind
{
    /// <summary>A field exactly as the log defines it.</summary>
    Raw = 1,

    /// <summary>A format-independent signal derived by an explicit, versioned rule.</summary>
    Normalized = 2,
}

/// <summary>One stored series (message type + instance) and where its chunks live.</summary>
public sealed record FlightSignalSeries
{
    public required Guid Id { get; init; }

    public required Guid FlightId { get; init; }

    public required Guid JobId { get; init; }

    public required string Message { get; init; }

    public required int Instance { get; init; }

    public required SignalKind Kind { get; init; }

    public required long RecordCount { get; init; }

    public long? FirstTimestampUs { get; init; }

    public long? LastTimestampUs { get; init; }

    public required int ColumnCount { get; init; }

    /// <summary>Chunk <c>i</c> is stored under <c>{StoragePrefix}{i:D6}.fls</c>.</summary>
    public required string StoragePrefix { get; init; }

    public string ChunkKey(int index) => $"{StoragePrefix}{index:D6}.fls";
}

/// <summary>Time range and size of one chunk, so that a query reads only the chunks it needs.</summary>
public sealed record SignalChunkInfo
{
    public required Guid SeriesId { get; init; }

    public required int Index { get; init; }

    public required long StartTimestampUs { get; init; }

    public required long EndTimestampUs { get; init; }

    public required int RecordCount { get; init; }

    public required int SizeBytes { get; init; }
}

/// <summary>A signal: one numeric field of a series, with its provenance and statistics.</summary>
public sealed record FlightSignal
{
    public const int NameMaxLength = 300;

    public required Guid Id { get; init; }

    public required Guid FlightId { get; init; }

    public required Guid SeriesId { get; init; }

    /// <summary>
    /// Raw: <c>Message.Field</c>, with <c>#n</c> after the message for instances other than 0
    /// (<c>ATT.Roll</c>, <c>sensor_accel#1.x</c>). Normalized: <c>group.field</c> (<c>attitude.roll</c>).
    /// </summary>
    public required string Name { get; init; }

    public required SignalKind Kind { get; init; }

    public required string Message { get; init; }

    public required int Instance { get; init; }

    public required string Field { get; init; }

    /// <summary>Column of the field in its series' chunks.</summary>
    public required int Column { get; init; }

    /// <summary>Type as written in the log, or <c>double</c> for normalized signals.</summary>
    public required string SourceType { get; init; }

    /// <summary>Unit as the log declares it (or as the normalization rule defines it); null when unknown.</summary>
    public string? Unit { get; init; }

    /// <summary>Multiplier the log declares but the stored values do not include; the API applies it when reading.</summary>
    public double? Multiplier { get; init; }

    /// <summary>Normalized signals: the original message, instance, fields and the conversion applied.</summary>
    public string? SourceMessage { get; init; }

    public int? SourceInstance { get; init; }

    public string? SourceFields { get; init; }

    public string? Conversion { get; init; }

    public string? NormalizationVersion { get; init; }

    public required long SampleCount { get; init; }

    /// <summary>Samples that are NaN or infinite (logged as invalid or not measured).</summary>
    public required long InvalidCount { get; init; }

    public double? Min { get; init; }

    public double? Max { get; init; }

    public long? FirstTimestampUs { get; init; }

    public long? LastTimestampUs { get; init; }
}

public enum FlightEventKind
{
    Message = 1,
    ModeChange = 2,
    Armed = 3,
    Disarmed = 4,
    Failsafe = 5,
    Error = 6,
    Event = 7,
    Dropout = 8,
}

public enum EventSeverity
{
    Emergency = 0,
    Alert = 1,
    Critical = 2,
    Error = 3,
    Warning = 4,
    Notice = 5,
    Info = 6,
    Debug = 7,
}

/// <summary>Something that happened at a point of log time (message, mode change, arming, failsafe...).</summary>
public sealed record FlightEvent
{
    public required Guid Id { get; init; }

    public required Guid FlightId { get; init; }

    public required long TimestampUs { get; init; }

    public required FlightEventKind Kind { get; init; }

    /// <summary>Null when the log does not give one (ArduPilot MSG).</summary>
    public EventSeverity? Severity { get; init; }

    /// <summary>Where the event came from (ULog topic, DataFlash message, <c>log</c>, <c>logger</c>).</summary>
    public required string Source { get; init; }

    public required string Text { get; init; }

    public IReadOnlyDictionary<string, string>? Data { get; init; }
}

/// <summary>A parameter value at log start (<see cref="TimestampUs"/> null) or a change during the flight.</summary>
public sealed record FlightParameter
{
    public required Guid Id { get; init; }

    public required Guid FlightId { get; init; }

    public required string Name { get; init; }

    public required double Value { get; init; }

    public double? DefaultValue { get; init; }

    public long? TimestampUs { get; init; }
}
