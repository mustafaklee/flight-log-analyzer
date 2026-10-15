using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlightLog.ParserTests;

internal static class Fixtures
{
    public static string Path(string relative) => System.IO.Path.Combine(AppContext.BaseDirectory, "fixtures", relative);

    public static FileStream Open(string relative) => File.OpenRead(Path(relative));

    public static T Expected<T>(string fileName) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(Path(System.IO.Path.Combine("expected", fileName + ".json"))), Json)!;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };
}

/// <summary>Aggregates of one field as produced by tools/fixtures/generate_expected.py.</summary>
internal sealed record FieldAggregate(long Count, long Nonfinite, double? Sum, double? Min, double? Max);

internal sealed record ExpectedSeries(string Message, int Instance, long Records, long? FirstTimestamp, long? LastTimestamp, Dictionary<string, FieldAggregate> Fields);

internal sealed record ExpectedULog(
    string File,
    long HeaderTimestamp,
    long LastTimestamp,
    bool Corrupt,
    Dictionary<string, string> Info,
    Dictionary<string, double> InitialParameters,
    JsonElement[][] ChangedParameters,
    JsonElement[][] LoggedMessages,
    int TaggedLoggedMessages,
    long[][] Dropouts,
    ExpectedSeries[] Series);

internal sealed record ExpectedDataFlash(
    string File,
    string? Firmware,
    Dictionary<string, double> Parameters,
    JsonElement[][] Messages,
    long[][] Modes,
    ExpectedSeries[] Series);

/// <summary>Computes the same aggregates as the oracle while the parser runs.</summary>
internal sealed class AggregatingSink : LogRecordSinkBase
{
    public Dictionary<(string Message, int Instance), SeriesAggregate> Series { get; } = [];

    public List<LogText> Texts { get; } = [];

    public List<LogParameter> Parameters { get; } = [];

    public Dictionary<string, string> Info { get; } = [];

    public List<(long Timestamp, int Duration)> Dropouts { get; } = [];

    public List<LogRecordCopy> Records { get; } = [];

    public Func<LogSeries, bool> KeepRecords { get; init; } = _ => false;

    public override void OnSeries(LogSeries series)
    {
        Series.Add((series.Message, series.Instance), new SeriesAggregate(series));
    }

    public override void OnRecord(LogRecord record)
    {
        Series[(record.Series.Message, record.Series.Instance)].Add(record);
        if (KeepRecords(record.Series))
        {
            Records.Add(new LogRecordCopy(record.Series, record.TimestampUs, record.Values.ToArray(), record.Texts.ToArray()));
        }
    }

    public override void OnText(LogText text) => Texts.Add(text);

    public override void OnParameter(LogParameter parameter) => Parameters.Add(parameter);

    public override void OnInfo(string key, string value) => Info[key] = value;

    public override void OnDropout(long timestampUs, int durationMs) => Dropouts.Add((timestampUs, durationMs));
}

internal sealed record LogRecordCopy(LogSeries Series, long TimestampUs, double[] Values, string?[] Texts)
{
    public double Value(string field) => Values[Series.IndexOf(field)];

    public string? Text(string field) => Texts[Series.IndexOf(field)];
}

internal sealed class SeriesAggregate(LogSeries series)
{
    private readonly ColumnAggregate[] _columns = series.Fields.Select(_ => new ColumnAggregate()).ToArray();

    public LogSeries Series { get; } = series;

    public long Records { get; private set; }

    public long FirstTimestamp { get; private set; }

    public long LastTimestamp { get; private set; }

    public void Add(LogRecord record)
    {
        if (Records == 0)
        {
            FirstTimestamp = record.TimestampUs;
        }

        LastTimestamp = record.TimestampUs;
        Records++;
        for (var i = 0; i < _columns.Length; i++)
        {
            if (Series.Fields[i].Kind == LogFieldKind.Numeric)
            {
                _columns[i].Add(record.Values[i]);
            }
        }
    }

    public ColumnAggregate Column(string field) => _columns[Series.IndexOf(field)];
}

internal sealed class ColumnAggregate
{
    private double _compensation;

    public long Count { get; private set; }

    public long Nonfinite { get; private set; }

    public double Sum { get; private set; }

    public double Min { get; private set; } = double.PositiveInfinity;

    public double Max { get; private set; } = double.NegativeInfinity;

    public void Add(double value)
    {
        Count++;
        if (!double.IsFinite(value))
        {
            Nonfinite++;
            return;
        }

        // Neumaier summation keeps the sum close to numpy's pairwise result.
        var t = Sum + value;
        _compensation += Math.Abs(Sum) >= Math.Abs(value) ? (Sum - t) + value : (value - t) + Sum;
        Sum = t;
        Min = Math.Min(Min, value);
        Max = Math.Max(Max, value);
    }

    public double CompensatedSum => Sum + _compensation;
}
