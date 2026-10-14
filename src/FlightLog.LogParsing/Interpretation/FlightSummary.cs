namespace FlightLog.LogParsing.Interpretation;

public enum FlightEventKind
{
    /// <summary>Text the vehicle logged.</summary>
    Message,

    ModeChange,
    Armed,
    Disarmed,

    /// <summary>A failsafe was triggered (or cleared, see <see cref="FlightEventRecord.Data"/>).</summary>
    Failsafe,

    /// <summary>An error reported by a subsystem.</summary>
    Error,

    /// <summary>A discrete event the autopilot logs (ArduPilot EV message).</summary>
    Event,

    /// <summary>The logger lost data.</summary>
    Dropout,
}

/// <summary>A point in time worth showing on the timeline. Texts are as logged; nothing is inferred.</summary>
public sealed record FlightEventRecord(
    long TimestampUs,
    FlightEventKind Kind,
    LogSeverity? Severity,
    string Source,
    string Text,
    IReadOnlyDictionary<string, string>? Data = null);

/// <summary>What the log says about the flight as a whole. Values the log does not contain stay null.</summary>
public sealed class FlightSummary
{
    public string? FirmwareVersion { get; set; }

    public string? VehicleType { get; set; }

    public string? Hardware { get; set; }

    /// <summary>First and last data timestamp (µs since boot).</summary>
    public long? FirstTimestampUs { get; set; }

    public long? LastTimestampUs { get; set; }

    public long? DurationUs => FirstTimestampUs is { } first && LastTimestampUs is { } last ? last - first : null;

    /// <summary>UTC time of boot (timestamp 0), derived from GPS time; null without a GPS time fix.</summary>
    public DateTimeOffset? BootTimeUtc { get; set; }

    /// <summary>UTC time of the first data timestamp, when <see cref="BootTimeUtc"/> is known.</summary>
    public DateTimeOffset? StartedAtUtc => BootTimeUtc is { } boot && FirstTimestampUs is { } first ? boot.AddTicks(first * 10) : null;

    /// <summary>Key/value facts from the log (software versions, board, UUIDs). Bounded in size.</summary>
    public Dictionary<string, string> Metadata { get; } = new(StringComparer.Ordinal);

    public List<FlightEventRecord> Events { get; } = [];

    /// <summary>Parameters: values at log start (timestamp null) and later changes.</summary>
    public List<LogParameter> Parameters { get; } = [];

    /// <summary>True when events or parameters were dropped because of the limits.</summary>
    public bool Truncated { get; set; }
}

/// <summary>Builds a <see cref="FlightSummary"/> from decoded records of one format.</summary>
public abstract class FlightInterpreter : LogRecordSinkBase
{
    public const int MaxEvents = 50_000;
    public const int MaxParameters = 50_000;
    public const int MaxMetadataEntries = 100;
    public const int MaxMetadataValueLength = 500;
    public const int MaxEventTextLength = 1000;

    protected FlightSummary Summary { get; } = new();

    /// <summary>Finishes the summary after the parser is done.</summary>
    public virtual FlightSummary Complete(ParserReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        Summary.FirstTimestampUs = report.FirstTimestampUs;
        Summary.LastTimestampUs = report.LastTimestampUs;
        Summary.Events.Sort((a, b) => a.TimestampUs.CompareTo(b.TimestampUs));
        return Summary;
    }

    public override void OnDropout(long timestampUs, int durationMs) =>
        AddEvent(new FlightEventRecord(timestampUs, FlightEventKind.Dropout, LogSeverity.Warning, "logger", $"Logger dropout of {durationMs} ms",
            new Dictionary<string, string> { ["durationMs"] = durationMs.ToString(System.Globalization.CultureInfo.InvariantCulture) }));

    protected void AddEvent(FlightEventRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (Summary.Events.Count >= MaxEvents)
        {
            Summary.Truncated = true;
            return;
        }

        Summary.Events.Add(record.Text.Length > MaxEventTextLength ? record with { Text = record.Text[..MaxEventTextLength] } : record);
    }

    protected void AddParameter(LogParameter parameter)
    {
        if (Summary.Parameters.Count >= MaxParameters)
        {
            Summary.Truncated = true;
            return;
        }

        Summary.Parameters.Add(parameter);
    }

    protected void SetMetadata(string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || (Summary.Metadata.Count >= MaxMetadataEntries && !Summary.Metadata.ContainsKey(key)))
        {
            return;
        }

        value = value.Trim();
        Summary.Metadata[key] = value.Length > MaxMetadataValueLength ? value[..MaxMetadataValueLength] : value;
    }

    /// <summary>GPS time (week + milliseconds of week) to UTC. GPS time is ahead of UTC by the leap seconds.</summary>
    protected static DateTimeOffset GpsToUtc(long week, double millisecondsOfWeek) =>
        GpsEpoch.AddDays(week * 7).AddMilliseconds(millisecondsOfWeek).AddSeconds(-LeapSecondsAt(GpsEpoch.AddDays(week * 7)));

    private static readonly DateTimeOffset GpsEpoch = new(1980, 1, 6, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// GPS-UTC offset. 18 s since 2017-01-01 (IERS Bulletin C: no leap second announced since); earlier logs use
    /// the historical value from the IERS table.
    /// </summary>
    private static int LeapSecondsAt(DateTimeOffset gpsTime) => gpsTime switch
    {
        _ when gpsTime >= new DateTimeOffset(2017, 1, 1, 0, 0, 0, TimeSpan.Zero) => 18,
        _ when gpsTime >= new DateTimeOffset(2015, 7, 1, 0, 0, 0, TimeSpan.Zero) => 17,
        _ when gpsTime >= new DateTimeOffset(2012, 7, 1, 0, 0, 0, TimeSpan.Zero) => 16,
        _ when gpsTime >= new DateTimeOffset(2009, 1, 1, 0, 0, 0, TimeSpan.Zero) => 15,
        _ when gpsTime >= new DateTimeOffset(2006, 1, 1, 0, 0, 0, TimeSpan.Zero) => 14,
        _ => 13,
    };
}
