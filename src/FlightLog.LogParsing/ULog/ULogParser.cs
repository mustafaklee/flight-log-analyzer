using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;

namespace FlightLog.LogParsing.ULog;

/// <summary>
/// Streaming reader for the PX4 ULog format (https://docs.px4.io/main/en/dev_log/ulog_file_format.html).
/// Behaviour follows the reference implementation pyulog where the specification leaves room:
/// data messages must match the subscribed format size exactly, trailing padding is not logged,
/// appended data is read from the offsets in the flag bits message and corrupt sections are skipped
/// by searching for the sync sequence.
/// </summary>
public sealed class ULogParser : IFlightLogParser
{
    public const string ParserName = "ulog";
    public const string ParserVersion = "1.0.0";

    private static readonly byte[] Magic = [0x55, 0x4C, 0x6F, 0x67, 0x01, 0x12, 0x35];
    private static readonly byte[] SyncBytes = [0x2F, 0x73, 0x13, 0x20, 0x25, 0x0C, 0xBB, 0x12];

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
        private readonly Dictionary<string, List<FormatField>> _formats = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Layout?> _layouts = new(StringComparer.Ordinal);
        private readonly Dictionary<ushort, Subscription> _subscriptions = [];
        private readonly Dictionary<(string Name, int MultiId), SeriesState> _series = [];
        private readonly HashSet<ushort> _missingIds = [];
        private readonly byte[] _payload = new byte[ushort.MaxValue];
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private bool _hasSync = true;
        private bool _inDataSection;
        private long _lastTimestamp;
        private List<long> _appendedOffsets = [];

        public ParserReport Run()
        {
            ReadHeader();
            _lastTimestamp = _report.HeaderTimestampUs ?? 0;
            ReadMessages();
            _report.SeriesCount = _series.Count;
            return _report;
        }

        private void ReadHeader()
        {
            Span<byte> header = stackalloc byte[16];
            if (!reader.TryRead(header))
            {
                throw new LogParseException("ulog.header_too_short", "The file is shorter than a ULog header.");
            }

            if (!header[..7].SequenceEqual(Magic))
            {
                throw new LogParseException("ulog.invalid_header", "The file does not start with the ULog magic bytes.");
            }

            if (header[7] > 1)
            {
                _report.Warn(limits, $"Unknown ULog version {header[7]}; reading it anyway.");
            }

            _report.HeaderTimestampUs = (long)BinaryPrimitives.ReadUInt64LittleEndian(header[8..]);
        }

        /// <summary>
        /// Reads all messages. With appended data, reading stops before the first message that would cross the
        /// next appended offset and continues at that offset (pyulog semantics).
        /// </summary>
        private void ReadMessages()
        {
            Span<byte> head = stackalloc byte[3];
            var section = 0;
            while (true)
            {
                if ((_report.MessageCount & 0xFFF) == 0)
                {
                    CheckBudget();
                }

                var start = reader.Position;
                var complete = reader.TryRead(head);
                var size = complete ? BinaryPrimitives.ReadUInt16LittleEndian(head) : 0;
                var payload = _payload.AsSpan(0, size);
                complete = complete && reader.TryRead(payload);
                var nextOffset = section < _appendedOffsets.Count ? _appendedOffsets[section] : long.MaxValue;
                if (!complete || reader.Position > nextOffset)
                {
                    if (nextOffset != long.MaxValue)
                    {
                        reader.Seek(nextOffset);
                        section++;
                        continue;
                    }

                    if (!complete && start < reader.Length)
                    {
                        _report.Truncated = true;
                    }

                    return;
                }

                _report.MessageCount++;
                var type = (char)head[2];
                try
                {
                    Handle(type, size, payload, start);
                }
                catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or FormatException or OverflowException)
                {
                    MarkCorrupt($"Malformed '{type}' message at offset {start}.");
                }
            }
        }

        private void Handle(char type, int size, ReadOnlySpan<byte> payload, long start)
        {
            switch (type)
            {
                case 'B':
                    HandleFlagBits(payload, start);
                    break;
                case 'F':
                    HandleFormat(payload);
                    break;
                case 'I':
                    HandleInfo(payload);
                    break;
                case 'M':
                    // Multi-part info (e.g. perf counters, boot console): not used.
                    break;
                case 'P':
                    HandleParameter(payload, defaultTypes: null);
                    break;
                case 'Q':
                    HandleParameter(payload[1..], defaultTypes: payload[0]);
                    break;
                case 'A':
                    _inDataSection = true;
                    HandleSubscription(payload);
                    break;
                case 'R':
                    // Unsubscribe: keep the stream, the id may be reused by a later 'A'.
                    break;
                case 'D':
                    HandleData(payload);
                    break;
                case 'L':
                    _inDataSection = true;
                    HandleLogging(payload, tagged: false);
                    break;
                case 'C':
                    _inDataSection = true;
                    HandleLogging(payload, tagged: true);
                    break;
                case 'S':
                    break;
                case 'O':
                    sink.OnDropout(_lastTimestamp, BinaryPrimitives.ReadUInt16LittleEndian(payload));
                    break;
                default:
                    HandleUnknown(type, size, start);
                    break;
            }
        }

        private void HandleFlagBits(ReadOnlySpan<byte> payload, long start)
        {
            if (start != 16)
            {
                _report.Warn(limits, "Flag bits message is not the first message.");
                return;
            }

            var incompat = payload.Slice(8, 8);
            if ((incompat[0] & ~1) != 0 || incompat[1..].IndexOfAnyExcept((byte)0) >= 0)
            {
                throw new LogParseException("ulog.unsupported_flags", "The log uses incompatible ULog features this parser does not know.");
            }

            if ((incompat[0] & 1) != 0 && payload.Length >= 40)
            {
                for (var i = 0; i < 3; i++)
                {
                    var offset = (long)BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(16 + (i * 8)));
                    if (offset != 0)
                    {
                        _appendedOffsets.Add(offset);
                    }
                }

                _appendedOffsets = [.. _appendedOffsets.Where(o => o > 0 && o <= reader.Length)];
            }
        }

        private void HandleFormat(ReadOnlySpan<byte> payload)
        {
            var text = TextDecoding.Decode(payload);
            var colon = text.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                MarkCorrupt("Format message without a name.");
                return;
            }

            if (_formats.Count >= limits.MaxFormats)
            {
                throw new LogParseException("parser.too_many_formats", $"The log defines more than {limits.MaxFormats} formats.");
            }

            var fields = new List<FormatField>();
            foreach (var part in text[(colon + 1)..].Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var space = part.IndexOf(' ', StringComparison.Ordinal);
                if (space <= 0)
                {
                    MarkCorrupt($"Malformed field '{part}' in format '{text[..colon]}'.");
                    return;
                }

                var typeText = part[..space];
                var name = part[(space + 1)..];
                var arraySize = 0;
                var bracket = typeText.IndexOf('[', StringComparison.Ordinal);
                if (bracket >= 0)
                {
                    arraySize = int.Parse(typeText.AsSpan(bracket + 1, typeText.IndexOf(']', StringComparison.Ordinal) - bracket - 1), CultureInfo.InvariantCulture);
                    typeText = typeText[..bracket];
                }

                fields.Add(new FormatField(typeText, arraySize, name));
            }

            _formats[text[..colon]] = fields;
        }

        private void HandleInfo(ReadOnlySpan<byte> payload)
        {
            if (ReadKeyValue(payload) is { } info && info.Value is not null)
            {
                sink.OnInfo(info.Key, info.Value);
            }
        }

        private void HandleParameter(ReadOnlySpan<byte> payload, byte? defaultTypes)
        {
            if (ReadKeyValue(payload) is not { } info || info.Number is not { } number)
            {
                return;
            }

            if (defaultTypes is { } types)
            {
                // Bit 0: system default. Bit 1: default of the current configuration (airframe).
                if ((types & 1) != 0)
                {
                    sink.OnParameter(new LogParameter(info.Key, double.NaN, null, number));
                }

                return;
            }

            sink.OnParameter(new LogParameter(info.Key, number, _inDataSection ? _lastTimestamp : null));
        }

        private static (string Key, string? Value, double? Number)? ReadKeyValue(ReadOnlySpan<byte> payload)
        {
            var keyLength = payload[0];
            var typeKey = TextDecoding.Decode(payload.Slice(1, keyLength));
            var space = typeKey.IndexOf(' ', StringComparison.Ordinal);
            if (space <= 0)
            {
                return null;
            }

            var type = typeKey[..space];
            var key = typeKey[(space + 1)..];
            var value = payload[(1 + keyLength)..];
            if (type.StartsWith("char[", StringComparison.Ordinal))
            {
                return (key, TextDecoding.Decode(value), null);
            }

            if (Primitive(type) is { } primitive && primitive != PrimitiveType.Chars && value.Length >= FieldSlot.SizeOf(primitive))
            {
                var number = new FieldSlot(0, primitive, 0, 0, 1).ReadNumber(value);
                return (key, number.ToString("R", CultureInfo.InvariantCulture), number);
            }

            return (key, null, null);
        }

        private void HandleSubscription(ReadOnlySpan<byte> payload)
        {
            var multiId = payload[0];
            var msgId = BinaryPrimitives.ReadUInt16LittleEndian(payload[1..]);
            var name = TextDecoding.Decode(payload[3..]);
            var layout = GetLayout(name);
            if (layout is null)
            {
                MarkCorrupt($"Subscription to '{name}' without a valid format.");
                return;
            }

            _subscriptions[msgId] = new Subscription(name, multiId, layout);
            _missingIds.Remove(msgId);
        }

        private void HandleData(ReadOnlySpan<byte> payload)
        {
            var msgId = BinaryPrimitives.ReadUInt16LittleEndian(payload);
            if (!_subscriptions.TryGetValue(msgId, out var subscription))
            {
                if (_missingIds.Add(msgId))
                {
                    _report.Warn(limits, $"Data for unknown message id {msgId}.");
                }

                _report.Corrupt = true;
                return;
            }

            var data = payload[2..];
            var layout = subscription.Layout;
            if (data.Length != layout.Size)
            {
                _report.Corrupt = true;
                return;
            }

            if (layout.TimestampOffset < 0)
            {
                return;
            }

            var timestamp = (long)BinaryPrimitives.ReadUInt64LittleEndian(data[layout.TimestampOffset..]);
            if (timestamp > _lastTimestamp)
            {
                _lastTimestamp = timestamp;
            }

            var state = GetSeries(subscription);
            var values = state.Values;
            var texts = state.Texts;
            foreach (var slot in layout.Slots)
            {
                if (slot.Type == PrimitiveType.Chars)
                {
                    values[slot.FieldIndex] = double.NaN;
                    texts[slot.FieldIndex] = slot.ReadText(data);
                }
                else
                {
                    values[slot.FieldIndex] = slot.ReadNumber(data);
                }
            }

            _report.RecordCount++;
            _report.ObserveTimestamp(timestamp);
            sink.OnRecord(new LogRecord(state.Series, timestamp, values, texts));
        }

        private void HandleLogging(ReadOnlySpan<byte> payload, bool tagged)
        {
            var level = payload[0];
            int? tag = tagged ? BinaryPrimitives.ReadUInt16LittleEndian(payload[1..]) : null;
            var at = tagged ? 3 : 1;
            var timestamp = (long)BinaryPrimitives.ReadUInt64LittleEndian(payload[at..]);
            var message = TextDecoding.Decode(payload[(at + 8)..]);
            LogSeverity? severity = level is >= (byte)'0' and <= (byte)'7' ? (LogSeverity)(level - '0') : null;
            sink.OnText(new LogText(timestamp, severity, message, tag));
        }

        private void HandleUnknown(char type, int size, long start)
        {
            if (type == '\0' || size == 0 || size > 10000)
            {
                // Looks like garbage: advance by one byte and look for the next sync marker.
                MarkCorrupt($"Corrupt message header at offset {start}.");
                reader.Seek(start + 1);
                if (_hasSync)
                {
                    FindSync(-1);
                }
            }
            else
            {
                // Unknown but plausible message type (newer logger): skip it, but a sync marker may hide inside.
                _report.Warn(limits, $"Skipped unknown message type '{type}'.");
                if (_hasSync)
                {
                    reader.Seek(reader.Position - size);
                    if (!FindSync(size))
                    {
                        reader.Seek(start + 3 + size);
                    }
                }
            }
        }

        /// <summary>Searches for the sync sequence; on success the reader is positioned after it.</summary>
        private bool FindSync(int lastBytes)
        {
            var origin = reader.Position;
            var limit = lastBytes < 0 ? long.MaxValue : origin + lastBytes;
            var matched = 0;
            while (reader.Position < limit)
            {
                var b = reader.ReadByte();
                if (b < 0)
                {
                    break;
                }

                matched = b == SyncBytes[matched] ? matched + 1 : (b == SyncBytes[0] ? 1 : 0);
                if (matched == SyncBytes.Length)
                {
                    _report.SkippedBytes += reader.Position - origin - SyncBytes.Length;
                    _report.Corrupt = true;
                    return true;
                }
            }

            reader.Seek(origin);
            if (lastBytes < 0)
            {
                _hasSync = false;
            }

            return false;
        }

        private SeriesState GetSeries(Subscription subscription)
        {
            var key = (subscription.Name, subscription.MultiId);
            if (_series.TryGetValue(key, out var state))
            {
                return state;
            }

            if (_series.Count >= limits.MaxSeries)
            {
                throw new LogParseException("parser.too_many_series", $"The log contains more than {limits.MaxSeries} message series.");
            }

            var series = new LogSeries(_series.Count, subscription.Name, subscription.MultiId, subscription.Layout.Fields);
            state = new SeriesState(series, new double[series.Fields.Count], subscription.Layout.HasText ? new string?[series.Fields.Count] : []);
            _series[key] = state;
            sink.OnSeries(series);
            return state;
        }

        private Layout? GetLayout(string name)
        {
            if (_layouts.TryGetValue(name, out var layout))
            {
                return layout;
            }

            var flat = new List<FlatField>();
            layout = Flatten(name, string.Empty, flat, depth: 0) ? BuildLayout(flat) : null;
            _layouts[name] = layout;
            return layout;
        }

        private bool Flatten(string typeName, string prefix, List<FlatField> output, int depth)
        {
            if (depth > limits.MaxNestingDepth || !_formats.TryGetValue(typeName, out var format))
            {
                return false;
            }

            foreach (var field in format)
            {
                var name = prefix + field.Name;
                if (Primitive(field.Type) is { } primitive)
                {
                    if (primitive == PrimitiveType.Chars)
                    {
                        output.Add(new FlatField(name, field.Type, PrimitiveType.Chars, Math.Max(field.ArraySize, 1), IsPadding(field.Name)));
                    }
                    else if (field.ArraySize > 0)
                    {
                        for (var i = 0; i < field.ArraySize; i++)
                        {
                            output.Add(new FlatField($"{name}[{i}]", field.Type, primitive, FieldSlot.SizeOf(primitive), IsPadding(field.Name)));
                        }
                    }
                    else
                    {
                        output.Add(new FlatField(name, field.Type, primitive, FieldSlot.SizeOf(primitive), IsPadding(field.Name)));
                    }
                }
                else if (field.ArraySize > 0)
                {
                    for (var i = 0; i < field.ArraySize; i++)
                    {
                        if (!Flatten(field.Type, $"{name}[{i}].", output, depth + 1))
                        {
                            return false;
                        }
                    }
                }
                else if (!Flatten(field.Type, name + ".", output, depth + 1))
                {
                    return false;
                }

                if (output.Count > limits.MaxFieldsPerSeries * 4)
                {
                    throw new LogParseException("parser.too_many_fields", $"Format '{typeName}' has too many fields.");
                }
            }

            return true;
        }

        private Layout BuildLayout(List<FlatField> flat)
        {
            // The logger does not write trailing top-level padding (same rule as pyulog); padding at the end of a
            // nested type is part of the message.
            while (flat.Count > 0 && flat[^1].Name.StartsWith("_padding", StringComparison.Ordinal))
            {
                flat.RemoveAt(flat.Count - 1);
            }

            var fields = new List<LogField>();
            var slots = new List<FieldSlot>();
            var offset = 0;
            var timestampOffset = -1;
            foreach (var f in flat)
            {
                if (f.Name == "timestamp" && f.Type == PrimitiveType.UInt64)
                {
                    timestampOffset = offset;
                }

                if (!f.IsPadding)
                {
                    var kind = f.Type == PrimitiveType.Chars ? LogFieldKind.Text : LogFieldKind.Numeric;
                    slots.Add(new FieldSlot(offset, f.Type, f.Size, fields.Count, 1));
                    fields.Add(new LogField(f.Name, kind, f.TypeName));
                }

                offset += f.Size;
            }

            if (fields.Count > limits.MaxFieldsPerSeries)
            {
                throw new LogParseException("parser.too_many_fields", $"A format has more than {limits.MaxFieldsPerSeries} fields.");
            }

            return new Layout(fields, [.. slots], offset, timestampOffset, fields.Exists(f => f.Kind == LogFieldKind.Text));
        }

        private static bool IsPadding(string name) => name.StartsWith("_padding", StringComparison.Ordinal);

        private static PrimitiveType? Primitive(string type) => type switch
        {
            "int8_t" => PrimitiveType.Int8,
            "uint8_t" => PrimitiveType.UInt8,
            "int16_t" => PrimitiveType.Int16,
            "uint16_t" => PrimitiveType.UInt16,
            "int32_t" => PrimitiveType.Int32,
            "uint32_t" => PrimitiveType.UInt32,
            "int64_t" => PrimitiveType.Int64,
            "uint64_t" => PrimitiveType.UInt64,
            "float" => PrimitiveType.Float32,
            "double" => PrimitiveType.Float64,
            "bool" => PrimitiveType.Bool,
            "char" => PrimitiveType.Chars,
            _ => null,
        };

        private void MarkCorrupt(string warning)
        {
            _report.Corrupt = true;
            _report.Warn(limits, warning);
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

    private sealed record FormatField(string Type, int ArraySize, string Name);

    private sealed record FlatField(string Name, string TypeName, PrimitiveType Type, int Size, bool IsPadding);

    private sealed record Layout(IReadOnlyList<LogField> Fields, FieldSlot[] Slots, int Size, int TimestampOffset, bool HasText);

    private sealed record Subscription(string Name, int MultiId, Layout Layout);

    private sealed record SeriesState(LogSeries Series, double[] Values, string?[] Texts);
}
