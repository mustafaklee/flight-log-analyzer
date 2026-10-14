namespace FlightLog.LogParsing.Normalization;

/// <summary>Where a normalized field comes from and how it was converted.</summary>
public sealed record NormalizedFieldSource(string Message, int Instance, string Fields, string Conversion);

/// <summary>
/// Derives the format-independent signals of <see cref="NormalizationVersion"/> (for example <c>attitude.roll</c>)
/// from the decoded records and forwards them as extra series named <c>norm.&lt;group&gt;</c>, next to the
/// original series that are passed through unchanged. Each rule is explicit and documented in
/// docs/architecture.md; a rule only applies when every source field it needs is present, so a missing
/// signal is never filled with zeros. Timestamps are the original timestamps of the source record.
/// </summary>
public sealed class SignalNormalizer : ILogRecordSink
{
    public const string NormalizationVersion = "1";
    public const string MessagePrefix = "norm.";

    /// <summary>Series ids of normalized series start here so that they never collide with parser ids.</summary>
    public const int FirstSeriesId = 1_000_000;

    private const double RadToDeg = 180.0 / Math.PI;

    private readonly ILogRecordSink _inner;
    private readonly IReadOnlyList<Rule> _rules;
    private readonly Dictionary<int, Binding> _bindings = [];
    private readonly HashSet<string> _boundGroups = new(StringComparer.Ordinal);
    private readonly Dictionary<LogField, NormalizedFieldSource> _sources = [];
    private double[] _buffer = new double[8];
    private int _nextId = FirstSeriesId;

    private SignalNormalizer(ILogRecordSink inner, IReadOnlyList<Rule> rules)
    {
        _inner = inner;
        _rules = rules;
    }

    public static SignalNormalizer ForPx4(ILogRecordSink inner) => new(inner ?? throw new ArgumentNullException(nameof(inner)), Px4Rules);

    public static SignalNormalizer ForArduPilot(ILogRecordSink inner) => new(inner ?? throw new ArgumentNullException(nameof(inner)), ArduPilotRules);

    /// <summary>Source and conversion of each normalized field, for the signal catalog.</summary>
    public NormalizedFieldSource? SourceOf(LogField field) => _sources.GetValueOrDefault(field);

    public static bool IsNormalized(LogSeries series) =>
        series?.Message.StartsWith(MessagePrefix, StringComparison.Ordinal) ?? throw new ArgumentNullException(nameof(series));

    public void OnSeries(LogSeries series)
    {
        ArgumentNullException.ThrowIfNull(series);
        _inner.OnSeries(series);

        foreach (var rule in _rules)
        {
            // One source per group: the first matching series (instance 0) wins for the whole log.
            if (series.Message != rule.Message || series.Instance != 0 || _boundGroups.Contains(rule.Group))
            {
                continue;
            }

            if (rule.Bind(series) is not { } mapping)
            {
                continue;
            }

            var fields = mapping.Outputs.Select(o => new LogField(o.Name, LogFieldKind.Numeric, "double") { Unit = o.Unit }).ToArray();
            var target = new LogSeries(_nextId++, MessagePrefix + rule.Group, 0, fields);
            for (var i = 0; i < fields.Length; i++)
            {
                _sources[fields[i]] = new NormalizedFieldSource(series.Message, series.Instance, mapping.Outputs[i].Source, mapping.Outputs[i].Conversion);
            }

            _boundGroups.Add(rule.Group);
            if (!_bindings.TryGetValue(series.Id, out var existing))
            {
                _bindings[series.Id] = new Binding([(target, mapping.Compute)]);
            }
            else
            {
                existing.Targets.Add((target, mapping.Compute));
            }

            _inner.OnSeries(target);
        }
    }

    public void OnRecord(LogRecord record)
    {
        _inner.OnRecord(record);
        if (!_bindings.TryGetValue(record.Series.Id, out var binding))
        {
            return;
        }

        foreach (var (target, compute) in binding.Targets)
        {
            if (_buffer.Length < target.Fields.Count)
            {
                _buffer = new double[target.Fields.Count];
            }

            var values = _buffer.AsSpan(0, target.Fields.Count);
            compute(record.Values, values);
            _inner.OnRecord(new LogRecord(target, record.TimestampUs, values, []));
        }
    }

    public void OnText(LogText text) => _inner.OnText(text);

    public void OnParameter(LogParameter parameter) => _inner.OnParameter(parameter);

    public void OnInfo(string key, string value) => _inner.OnInfo(key, value);

    public void OnDropout(long timestampUs, int durationMs) => _inner.OnDropout(timestampUs, durationMs);

    /// <summary>Quaternion (w, x, y, z) to Euler angles in degrees, ZYX order, as PX4's matrix::Eulerf.</summary>
    internal static (double Roll, double Pitch, double Yaw) QuaternionToEulerDegrees(double w, double x, double y, double z)
    {
        var roll = Math.Atan2(2 * ((w * x) + (y * z)), 1 - (2 * ((x * x) + (y * y))));
        var sinPitch = Math.Clamp(2 * ((w * y) - (z * x)), -1.0, 1.0);
        var pitch = Math.Asin(sinPitch);
        var yaw = Math.Atan2(2 * ((w * z) + (x * y)), 1 - (2 * ((y * y) + (z * z))));
        return (roll * RadToDeg, pitch * RadToDeg, yaw * RadToDeg);
    }

    /// <summary>Wraps an angle in degrees into [-180, 180).</summary>
    internal static double WrapDegrees(double degrees) =>
        double.IsFinite(degrees) ? degrees - (360.0 * Math.Floor((degrees + 180.0) / 360.0)) : degrees;

    private delegate void Compute(ReadOnlySpan<double> source, Span<double> target);

    private sealed record Output(string Name, string Unit, string Source, string Conversion);

    private sealed record Mapping(Output[] Outputs, Compute Compute);

    private sealed record Rule(string Group, string Message, Func<LogSeries, Mapping?> Bind);

    private sealed record Binding(List<(LogSeries Target, Compute Compute)> Targets);

    private static int[]? Indices(LogSeries series, params string[] names)
    {
        var result = new int[names.Length];
        for (var i = 0; i < names.Length; i++)
        {
            result[i] = series.IndexOf(names[i]);
            if (result[i] < 0 || series.Fields[result[i]].Kind != LogFieldKind.Numeric)
            {
                return null;
            }
        }

        return result;
    }

    private static readonly Rule[] Px4Rules =
    [
        new("attitude", "vehicle_attitude", s =>
        {
            if (Indices(s, "q[0]", "q[1]", "q[2]", "q[3]") is not { } q)
            {
                return null;
            }

            const string conversion = "quaternion (w, x, y, z) to Euler ZYX; rad to deg";
            return new Mapping(
                [
                    new("roll", "deg", "q[0..3]", conversion),
                    new("pitch", "deg", "q[0..3]", conversion),
                    new("yaw", "deg", "q[0..3]", conversion + "; range -180..180"),
                ],
                (src, dst) =>
                {
                    (dst[0], dst[1], dst[2]) = QuaternionToEulerDegrees(src[q[0]], src[q[1]], src[q[2]], src[q[3]]);
                    dst[2] = WrapDegrees(dst[2]);
                });
        }),
        new("gps", "vehicle_gps_position", s =>
        {
            // PX4 1.14+ logs degrees and metres as doubles; earlier versions integers in 1e-7 deg and mm.
            var modern = Indices(s, "latitude_deg", "longitude_deg", "altitude_msl_m");
            var legacy = modern is null ? Indices(s, "lat", "lon", "alt") : null;
            var common = Indices(s, "fix_type", "satellites_used", "hdop", "vel_m_s");
            if ((modern ?? legacy) is not { } position || common is null)
            {
                return null;
            }

            var scales = modern is not null ? (Lat: 1.0, Alt: 1.0) : (Lat: 1e-7, Alt: 1e-3);
            var names = modern is not null ? ("latitude_deg", "longitude_deg", "altitude_msl_m") : ("lat", "lon", "alt");
            var latConversion = modern is not null ? "none" : "1e-7 deg to deg";
            return new Mapping(
                [
                    new("lat", "deg", names.Item1, latConversion),
                    new("lon", "deg", names.Item2, latConversion),
                    new("alt_msl", "m", names.Item3, modern is not null ? "none" : "mm to m"),
                    new("fix_type", "", "fix_type", "none (0-1 no fix, 2 2D, 3 3D, 4 DGPS, 5 RTK float, 6 RTK fixed)"),
                    new("satellites", "", "satellites_used", "none"),
                    new("hdop", "", "hdop", "none"),
                    new("ground_speed", "m/s", "vel_m_s", "none"),
                ],
                (src, dst) =>
                {
                    dst[0] = src[position[0]] * scales.Lat;
                    dst[1] = src[position[1]] * scales.Lat;
                    dst[2] = src[position[2]] * scales.Alt;
                    dst[3] = src[common[0]];
                    dst[4] = src[common[1]];
                    dst[5] = src[common[2]];
                    dst[6] = src[common[3]];
                });
        }),
        new("battery", "battery_status", s =>
        {
            if (Indices(s, "voltage_v", "current_a", "remaining") is not { } b)
            {
                return null;
            }

            return new Mapping(
                [
                    new("voltage", "V", "voltage_v", "none"),
                    new("current", "A", "current_a", "none; -1 (not measured) becomes invalid"),
                    new("remaining", "%", "remaining", "fraction to percent; negative (unknown) becomes invalid"),
                ],
                (src, dst) =>
                {
                    dst[0] = src[b[0]];
                    dst[1] = src[b[1]] < 0 ? double.NaN : src[b[1]];
                    dst[2] = src[b[2]] < 0 ? double.NaN : src[b[2]] * 100.0;
                });
        }),
    ];

    private static readonly Rule[] ArduPilotRules =
    [
        new("attitude", "ATT", s =>
        {
            if (Indices(s, "Roll", "Pitch", "Yaw") is not { } a)
            {
                return null;
            }

            return new Mapping(
                [
                    new("roll", "deg", "Roll", "none"),
                    new("pitch", "deg", "Pitch", "none"),
                    new("yaw", "deg", "Yaw", "0..360 wrapped to -180..180"),
                ],
                (src, dst) =>
                {
                    dst[0] = src[a[0]];
                    dst[1] = src[a[1]];
                    dst[2] = WrapDegrees(src[a[2]]);
                });
        }),
        new("gps", "GPS", s =>
        {
            if (Indices(s, "Lat", "Lng", "Alt", "Status", "NSats", "HDop", "Spd") is not { } g)
            {
                return null;
            }

            return new Mapping(
                [
                    new("lat", "deg", "Lat", "none"),
                    new("lon", "deg", "Lng", "none"),
                    new("alt_msl", "m", "Alt", "none"),
                    new("fix_type", "", "Status", "none (0 no GPS, 1 no fix, 2 2D, 3 3D, 4 DGPS, 5 RTK float, 6 RTK fixed)"),
                    new("satellites", "", "NSats", "none"),
                    new("hdop", "", "HDop", "none"),
                    new("ground_speed", "m/s", "Spd", "none"),
                ],
                (src, dst) =>
                {
                    for (var i = 0; i < g.Length; i++)
                    {
                        dst[i] = src[g[i]];
                    }
                });
        }),
        new("battery", "BAT", s =>
        {
            if (Indices(s, "Volt", "Curr", "RemPct") is not { } b)
            {
                return null;
            }

            return new Mapping(
                [
                    new("voltage", "V", "Volt", "none"),
                    new("current", "A", "Curr", "none"),
                    new("remaining", "%", "RemPct", "none; 255 (unknown) becomes invalid"),
                ],
                (src, dst) =>
                {
                    dst[0] = src[b[0]];
                    dst[1] = src[b[1]];
                    dst[2] = src[b[2]] >= 255 ? double.NaN : src[b[2]];
                });
        }),
    ];
}
