using System.Buffers.Binary;
using System.Diagnostics;

namespace FlightLog.LogParsing.DataFlash;

/// <summary>
/// Reader for ArduPilot DataFlash binary logs (.bin). The format is self-describing: <c>FMT</c> messages define
/// every message type, <c>FMTU</c>/<c>UNIT</c>/<c>MULT</c> describe units and instance fields
/// (https://ardupilot.org/dev/docs/code-overview-adding-a-new-log-message.html).
/// Decoding follows pymavlink's <c>DFReader</c>: bytes that do not start a known message are skipped one at a
/// time, the format characters c/C/e/E/L carry their scaling, and the field marked <c>#</c> in FMTU is the
/// instance of a message.
/// </summary>
public sealed class DataFlashParser : IFlightLogParser
{
    public const string ParserName = "dataflash";
    public const string ParserVersion = "1.0.0";

    private const byte Head1 = 0xA3;
    private const byte Head2 = 0x95;
    private const byte FmtType = 0x80;

    public string Name => ParserName;

    public string Version => ParserVersion;

    public ParserReport Parse(Stream input, ILogRecordSink sink, ParserLimits limits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(limits);
        return new Session(new SeekableReader(input), sink, limits, cancellationToken).Run();
    }

    private sealed class Session(SeekableReader reader, ILogRecordSink sink, ParserLimits limits, CancellationToken cancellationToken)
    {
        private readonly ParserReport _report = new() { Parser = ParserName, ParserVersion = ParserVersion };
        private readonly MessageFormat?[] _formats = new MessageFormat?[256];
        private readonly Dictionary<(MessageFormat Format, int Instance), SeriesState> _series = [];
        private readonly Dictionary<string, List<SeriesState>> _seriesByName = new(StringComparer.Ordinal);
        private readonly Dictionary<char, string> _units = [];
        private readonly Dictionary<char, double> _multipliers = [];
        private readonly byte[] _payload = new byte[256];
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _lastTimestamp;
        private int _formatCount;

        public ParserReport Run()
        {
            _formats[FmtType] = MessageFormat.Create(FmtType, "FMT", 89, "BBnNZ", "Type,Length,Name,Format,Columns")!;
            Span<byte> head = stackalloc byte[3];
            long? skipStart = null;
            while (true)
            {
                if ((_report.MessageCount & 0xFFF) == 0)
                {
                    CheckBudget();
                }

                var start = reader.Position;
                if (!reader.TryRead(head))
                {
                    break;
                }

                var format = head[0] == Head1 && head[1] == Head2 ? _formats[head[2]] : null;
                if (format is null)
                {
                    // Not the start of a known message: advance one byte (pymavlink behaviour).
                    skipStart ??= start;
                    reader.Seek(start + 1);
                    continue;
                }

                if (skipStart is { } skipped)
                {
                    _report.SkippedBytes += start - skipped;
                    _report.Corrupt = true;
                    _report.Warn(limits, $"Skipped {start - skipped} bytes at offset {skipped}.");
                    skipStart = null;
                }

                var body = _payload.AsSpan(0, format.Length - 3);
                if (!reader.TryRead(body))
                {
                    _report.Truncated = true;
                    break;
                }

                if (!format.IsConsistent)
                {
                    // Declared length does not match the field types; pymavlink skips the header and rescans.
                    _report.Corrupt = true;
                    reader.Seek(start + 3);
                    continue;
                }

                _report.MessageCount++;
                Decode(format, body);
            }

            if (skipStart is { } trailing)
            {
                _report.SkippedBytes += reader.Position - trailing;
            }

            ResolveUnits();
            _report.SeriesCount = _series.Count;
            return _report;
        }

        private void Decode(MessageFormat format, ReadOnlySpan<byte> body)
        {
            var values = format.Values;
            var texts = format.Texts;
            foreach (var slot in format.Slots)
            {
                if (slot.Type == PrimitiveType.Chars)
                {
                    values[slot.FieldIndex] = double.NaN;
                    texts[slot.FieldIndex] = slot.ReadText(body);
                }
                else if (slot.Type == PrimitiveType.Skip)
                {
                    values[slot.FieldIndex] = double.NaN;
                }
                else
                {
                    values[slot.FieldIndex] = slot.ReadNumber(body);
                }
            }

            switch (format.Name)
            {
                case "FMT":
                    DefineFormat(values, texts);
                    break;
                case "FMTU":
                    ApplyUnits(values, texts);
                    break;
                case "UNIT":
                    if (texts[2] is { } label && values[1] is var id && !double.IsNaN(id))
                    {
                        _units[(char)(byte)(sbyte)id] = label;
                    }

                    break;
                case "MULT":
                    if (!double.IsNaN(values[1]))
                    {
                        _multipliers[(char)(byte)(sbyte)values[1]] = values[2];
                    }

                    break;
            }

            long timestamp;
            if (format.TimeUsIndex >= 0)
            {
                timestamp = (long)values[format.TimeUsIndex];
                _report.ObserveTimestamp(timestamp);
                _lastTimestamp = timestamp;
            }
            else if (format.TimeMsIndex >= 0)
            {
                timestamp = (long)values[format.TimeMsIndex] * 1000;
                _report.ObserveTimestamp(timestamp);
                _lastTimestamp = timestamp;
            }
            else
            {
                timestamp = _lastTimestamp;
            }

            var instance = format.InstanceIndex >= 0 && double.IsFinite(values[format.InstanceIndex]) ? (int)values[format.InstanceIndex] : 0;
            var state = GetSeries(format, instance);
            _report.RecordCount++;
            sink.OnRecord(new LogRecord(state.Series, timestamp, values, texts));
        }

        private void DefineFormat(double[] values, string?[] texts)
        {
            var type = (int)values[0];
            var length = (int)values[1];
            var name = texts[2] ?? string.Empty;
            var formatChars = texts[3] ?? string.Empty;
            var columns = texts[4] ?? string.Empty;
            if (++_formatCount > limits.MaxFormats)
            {
                throw new LogParseException("parser.too_many_formats", $"The log defines more than {limits.MaxFormats} formats.");
            }

            var format = MessageFormat.Create(type, name, length, formatChars, columns);
            if (format is null)
            {
                _report.Corrupt = true;
                _report.Warn(limits, $"Unsupported format for message '{name}' ({formatChars}).");
                return;
            }

            if (format.Fields.Count > limits.MaxFieldsPerSeries)
            {
                throw new LogParseException("parser.too_many_fields", $"Message '{name}' has more than {limits.MaxFieldsPerSeries} fields.");
            }

            // A redefinition keeps the instance and unit information (pymavlink does the same).
            if (_formats[type] is { } old)
            {
                format.CopyAnnotations(old);
            }

            _formats[type] = format;
        }

        private void ApplyUnits(double[] values, string?[] texts)
        {
            var type = (int)values[1];
            if (type is < 0 or > 255 || _formats[type] is not { } format)
            {
                return;
            }

            format.SetAnnotations(texts[2] ?? string.Empty, texts[3] ?? string.Empty);
        }

        private SeriesState GetSeries(MessageFormat format, int instance)
        {
            if (_series.TryGetValue((format, instance), out var state))
            {
                return state;
            }

            // A redefinition of the same layout continues the existing series.
            if (_seriesByName.TryGetValue(format.Name, out var sameName)
                && sameName.Find(s => s.Series.Instance == instance && s.Format.HasSameLayout(format)) is { } existing)
            {
                _series[(format, instance)] = existing;
                return existing;
            }

            if (_series.Count >= limits.MaxSeries)
            {
                throw new LogParseException("parser.too_many_series", $"The log contains more than {limits.MaxSeries} message series.");
            }

            var fields = format.Fields.Select(f => new LogField(f.Name, f.Kind, f.TypeName)).ToList();
            var series = new LogSeries(_seriesByName.Values.Sum(l => l.Count), format.Name, instance, fields);
            state = new SeriesState(series, format);
            _series[(format, instance)] = state;
            if (!_seriesByName.TryGetValue(format.Name, out var list))
            {
                list = [];
                _seriesByName[format.Name] = list;
            }

            list.Add(state);
            sink.OnSeries(series);
            return state;
        }

        /// <summary>Attaches units and pending multipliers once all UNIT/MULT/FMTU messages are known.</summary>
        private void ResolveUnits()
        {
            foreach (var state in _seriesByName.Values.SelectMany(l => l))
            {
                var format = state.Format;
                for (var i = 0; i < state.Series.Fields.Count; i++)
                {
                    var field = state.Series.Fields[i];
                    var unitId = i < format.UnitIds.Length ? format.UnitIds[i] : '-';
                    var multId = i < format.MultIds.Length ? format.MultIds[i] : '-';
                    if (unitId != '#' && _units.TryGetValue(unitId, out var label) && label.Length > 0)
                    {
                        field.Unit = label;
                    }

                    if (field.Kind == LogFieldKind.Numeric && !format.Fields[i].SelfScaled && _multipliers.TryGetValue(multId, out var multiplier))
                    {
                        multiplier = SnapToPowerOfTen(multiplier);
                        if (multiplier is not 0 and not 1)
                        {
                            field.Multiplier = multiplier;
                        }
                    }
                }
            }
        }

        /// <summary>MULT values are logged as single precision (0.01 becomes 0.0099999998); restore exact powers of ten.</summary>
        private static double SnapToPowerOfTen(double value)
        {
            if (value <= 0)
            {
                return value;
            }

            var exponent = Math.Log10(value);
            var rounded = Math.Round(exponent);
            return Math.Abs(exponent - rounded) < 1e-6 ? Math.Pow(10, rounded) : Math.Round(value, 6);
        }

        private void CheckBudget()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_clock.Elapsed > limits.Timeout)
            {
                throw new LogParseException("parser.timeout", $"Parsing took longer than {limits.Timeout}.");
            }
        }
    }

    private sealed record SeriesState(LogSeries Series, MessageFormat Format);

    private sealed record FormatField(string Name, LogFieldKind Kind, string TypeName, bool SelfScaled);

    private sealed class MessageFormat
    {
        private MessageFormat(int type, string name, int length, IReadOnlyList<FormatField> fields, FieldSlot[] slots, bool consistent)
        {
            Type = type;
            Name = name;
            Length = length;
            Fields = fields;
            Slots = slots;
            IsConsistent = consistent;
            Values = new double[fields.Count];
            Texts = new string?[fields.Count];
            TimeUsIndex = IndexOf("TimeUS");
            TimeMsIndex = IndexOf("TimeMS");
        }

        public int Type { get; }

        public string Name { get; }

        public int Length { get; }

        public IReadOnlyList<FormatField> Fields { get; }

        public FieldSlot[] Slots { get; }

        /// <summary>The declared length matches the field types.</summary>
        public bool IsConsistent { get; }

        public double[] Values { get; }

        public string?[] Texts { get; }

        public int TimeUsIndex { get; }

        public int TimeMsIndex { get; }

        public int InstanceIndex { get; private set; } = -1;

        public string UnitIds { get; private set; } = string.Empty;

        public string MultIds { get; private set; } = string.Empty;

        public static MessageFormat? Create(int type, string name, int length, string formatChars, string columns)
        {
            var names = columns.Length == 0 ? [] : columns.Split(',');
            if (names.Length != formatChars.Length || length < 3)
            {
                return null;
            }

            var fields = new List<FormatField>(names.Length);
            var slots = new FieldSlot[names.Length];
            var offset = 0;
            for (var i = 0; i < names.Length; i++)
            {
                var c = formatChars[i];
                var (primitive, size, scale) = c switch
                {
                    'b' or 'M' => (PrimitiveType.Int8, 1, 1.0),
                    'B' => (PrimitiveType.UInt8, 1, 1.0),
                    'h' => (PrimitiveType.Int16, 2, 1.0),
                    'H' => (PrimitiveType.UInt16, 2, 1.0),
                    'i' => (PrimitiveType.Int32, 4, 1.0),
                    'I' => (PrimitiveType.UInt32, 4, 1.0),
                    'q' => (PrimitiveType.Int64, 8, 1.0),
                    'Q' => (PrimitiveType.UInt64, 8, 1.0),
                    'f' => (PrimitiveType.Float32, 4, 1.0),
                    'd' => (PrimitiveType.Float64, 8, 1.0),
                    'g' => (PrimitiveType.Float16, 2, 1.0),
                    'c' => (PrimitiveType.Int16, 2, 0.01),
                    'C' => (PrimitiveType.UInt16, 2, 0.01),
                    'e' => (PrimitiveType.Int32, 4, 0.01),
                    'E' => (PrimitiveType.UInt32, 4, 0.01),
                    'L' => (PrimitiveType.Int32, 4, 1e-7),
                    'n' => (PrimitiveType.Chars, 4, 1.0),
                    'N' => (PrimitiveType.Chars, 16, 1.0),
                    'Z' => (PrimitiveType.Chars, 64, 1.0),
                    'a' => (PrimitiveType.Skip, 64, 1.0),
                    _ => (PrimitiveType.Skip, -1, 1.0),
                };
                if (size < 0)
                {
                    return null;
                }

                var kind = primitive switch
                {
                    PrimitiveType.Chars => LogFieldKind.Text,
                    PrimitiveType.Skip => LogFieldKind.Ignored,
                    _ => LogFieldKind.Numeric,
                };
                fields.Add(new FormatField(names[i], kind, c.ToString(), scale != 1));
                slots[i] = new FieldSlot(offset, primitive, size, i, scale);
                offset += size;
            }

            return new MessageFormat(type, name, length, fields, slots, offset == length - 3);
        }

        public void SetAnnotations(string unitIds, string multIds)
        {
            UnitIds = unitIds;
            MultIds = multIds;
            var instance = unitIds.IndexOf('#', StringComparison.Ordinal);
            if (instance >= 0 && instance < Fields.Count)
            {
                InstanceIndex = instance;
            }
        }

        public void CopyAnnotations(MessageFormat old)
        {
            UnitIds = old.UnitIds;
            MultIds = old.MultIds;
            if (old.InstanceIndex >= 0)
            {
                InstanceIndex = IndexOf(old.Fields[old.InstanceIndex].Name);
            }
        }

        public bool HasSameLayout(MessageFormat other) =>
            Fields.Count == other.Fields.Count && Fields.Zip(other.Fields).All(p => p.First.Name == p.Second.Name && p.First.TypeName == p.Second.TypeName);

        private int IndexOf(string column)
        {
            for (var i = 0; i < Fields.Count; i++)
            {
                if (Fields[i].Name == column)
                {
                    return i;
                }
            }

            return -1;
        }
    }
}
