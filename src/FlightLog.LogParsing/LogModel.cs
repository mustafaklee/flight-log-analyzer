namespace FlightLog.LogParsing;

/// <summary>How a decoded field can be used.</summary>
public enum LogFieldKind
{
    /// <summary>A number; becomes a signal.</summary>
    Numeric,

    /// <summary>A character string (names, messages, versions).</summary>
    Text,

    /// <summary>Decoded but not exposed (padding, unsupported array encodings).</summary>
    Ignored,
}

/// <summary>One field of a logged message as described by the log's own format definitions.</summary>
public sealed class LogField
{
    internal LogField(string name, LogFieldKind kind, string typeName)
    {
        Name = name;
        Kind = kind;
        TypeName = typeName;
    }

    /// <summary>Flattened field name, e.g. <c>q[0]</c>, <c>accel.x</c> (ULog) or <c>Roll</c> (DataFlash).</summary>
    public string Name { get; }

    public LogFieldKind Kind { get; }

    /// <summary>The type as written in the log (<c>float</c>, <c>uint64_t</c>, DataFlash format character).</summary>
    public string TypeName { get; }

    /// <summary>Unit as declared by the log itself (DataFlash FMTU/UNIT); null when the log does not say.</summary>
    public string? Unit { get; internal set; }

    /// <summary>
    /// Multiplier that the log declares (DataFlash FMTU/MULT) and that is not yet applied to the decoded value.
    /// Null when there is none or when the field type already implies the scaling.
    /// </summary>
    public double? Multiplier { get; internal set; }
}

/// <summary>
/// A sequence of records of one message type and instance (ULog topic + multi id, DataFlash type + instance).
/// </summary>
public sealed class LogSeries
{
    private readonly Dictionary<string, int> _index;

    internal LogSeries(int id, string message, int instance, IReadOnlyList<LogField> fields)
    {
        Id = id;
        Message = message;
        Instance = instance;
        Fields = fields;
        _index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < fields.Count; i++)
        {
            _index.TryAdd(fields[i].Name, i);
        }
    }

    public int Id { get; }

    public string Message { get; }

    public int Instance { get; }

    public IReadOnlyList<LogField> Fields { get; }

    /// <summary>Index of the named field, or -1.</summary>
    public int IndexOf(string fieldName) => _index.TryGetValue(fieldName, out var i) ? i : -1;
}

/// <summary>
/// One decoded record. Values are only valid during the sink callback; sinks copy what they keep.
/// Text fields have <see cref="double.NaN"/> in <see cref="Values"/> and their content in <see cref="Texts"/>.
/// </summary>
public readonly ref struct LogRecord
{
    internal LogRecord(LogSeries series, long timestampUs, ReadOnlySpan<double> values, ReadOnlySpan<string?> texts)
    {
        Series = series;
        TimestampUs = timestampUs;
        Values = values;
        Texts = texts;
    }

    public LogSeries Series { get; }

    /// <summary>Microseconds since boot, as logged.</summary>
    public long TimestampUs { get; }

    public ReadOnlySpan<double> Values { get; }

    /// <summary>Same length as the field list when the series has text fields, otherwise empty.</summary>
    public ReadOnlySpan<string?> Texts { get; }

    public double Value(string field)
    {
        var i = Series.IndexOf(field);
        return i < 0 ? double.NaN : Values[i];
    }

    public string? Text(string field)
    {
        var i = Series.IndexOf(field);
        return i < 0 || Texts.IsEmpty ? null : Texts[i];
    }
}

/// <summary>Severity of a logged text message (syslog levels, as used by both PX4 and MAVLink).</summary>
public enum LogSeverity
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

/// <summary>A free-text message the vehicle wrote into the log.</summary>
public sealed record LogText(long TimestampUs, LogSeverity? Severity, string Message, int? Tag = null);

/// <summary>A parameter value; <see cref="TimestampUs"/> is null for values present at log start.</summary>
public sealed record LogParameter(string Name, double Value, long? TimestampUs, double? DefaultValue = null);

/// <summary>Receives what a parser decodes, in file order.</summary>
public interface ILogRecordSink
{
    /// <summary>Called once per series, before its first record.</summary>
    void OnSeries(LogSeries series);

    void OnRecord(LogRecord record);

    /// <summary>Text messages that are not part of a regular series (ULog logging messages).</summary>
    void OnText(LogText text);

    /// <summary>Parameters that are not part of a regular series (ULog parameter messages).</summary>
    void OnParameter(LogParameter parameter);

    /// <summary>Key/value information (ULog info messages).</summary>
    void OnInfo(string key, string value);

    /// <summary>The logger lost data at this time (ULog dropout messages).</summary>
    void OnDropout(long timestampUs, int durationMs);
}

/// <summary>Base sink that ignores everything; derive and override what is needed.</summary>
public abstract class LogRecordSinkBase : ILogRecordSink
{
    public virtual void OnSeries(LogSeries series)
    {
    }

    public virtual void OnRecord(LogRecord record)
    {
    }

    public virtual void OnText(LogText text)
    {
    }

    public virtual void OnParameter(LogParameter parameter)
    {
    }

    public virtual void OnInfo(string key, string value)
    {
    }

    public virtual void OnDropout(long timestampUs, int durationMs)
    {
    }
}

/// <summary>Forwards every callback to several sinks in order.</summary>
public sealed class CompositeSink(params ILogRecordSink[] sinks) : ILogRecordSink
{
    public void OnSeries(LogSeries series)
    {
        foreach (var s in sinks)
        {
            s.OnSeries(series);
        }
    }

    public void OnRecord(LogRecord record)
    {
        foreach (var s in sinks)
        {
            s.OnRecord(record);
        }
    }

    public void OnText(LogText text)
    {
        foreach (var s in sinks)
        {
            s.OnText(text);
        }
    }

    public void OnParameter(LogParameter parameter)
    {
        foreach (var s in sinks)
        {
            s.OnParameter(parameter);
        }
    }

    public void OnInfo(string key, string value)
    {
        foreach (var s in sinks)
        {
            s.OnInfo(key, value);
        }
    }

    public void OnDropout(long timestampUs, int durationMs)
    {
        foreach (var s in sinks)
        {
            s.OnDropout(timestampUs, durationMs);
        }
    }
}
