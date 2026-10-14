namespace FlightLog.LogParsing.Signals;

/// <summary>One encoded chunk, handed to the output as soon as it is complete.</summary>
public sealed record SignalChunk(int SeriesId, int Index, long StartTimestampUs, long EndTimestampUs, int RecordCount, byte[] Data);

/// <summary>Statistics of one numeric field over the whole log.</summary>
public sealed class SignalFieldStats
{
    internal SignalFieldStats(LogField field, int column)
    {
        Field = field;
        Column = column;
    }

    public LogField Field { get; }

    /// <summary>Column of the field in the series' chunks.</summary>
    public int Column { get; }

    /// <summary>Records that carry this field.</summary>
    public long SampleCount { get; internal set; }

    /// <summary>Samples that are NaN or infinite: logged as invalid or not measured.</summary>
    public long InvalidCount { get; internal set; }

    /// <summary>Minimum of the valid samples; null when there are none.</summary>
    public double? Min { get; internal set; }

    public double? Max { get; internal set; }
}

/// <summary>What was stored for one series.</summary>
public sealed class SignalSeriesStats
{
    internal SignalSeriesStats(LogSeries series, IReadOnlyList<SignalFieldStats> fields)
    {
        Series = series;
        Fields = fields;
    }

    public LogSeries Series { get; }

    public IReadOnlyList<SignalFieldStats> Fields { get; }

    public long RecordCount { get; internal set; }

    /// <summary>Earliest and latest timestamp of the series.</summary>
    public long? FirstTimestampUs { get; internal set; }

    public long? LastTimestampUs { get; internal set; }

    public int ChunkCount { get; internal set; }
}

/// <summary>
/// Buffers the numeric fields of every series column-wise and emits encoded chunks
/// (<see cref="SignalChunkCodec"/>) of at most <see cref="MaxRecordsPerChunk"/> records. Memory stays bounded:
/// a series is flushed when its buffer reaches <see cref="MaxBytesPerSeries"/>, and every buffer is flushed when
/// all of them together exceed <see cref="MaxBufferedBytes"/>. Text and ignored fields are not stored here.
/// </summary>
public sealed class SignalChunkSink(Action<SignalChunk> output) : LogRecordSinkBase
{
    public const int MaxRecordsPerChunk = 16_384;
    public const long MaxBytesPerSeries = 2 * 1024 * 1024;
    public const long MaxBufferedBytes = 96 * 1024 * 1024;

    private readonly Action<SignalChunk> _output = output ?? throw new ArgumentNullException(nameof(output));
    private readonly Dictionary<int, SeriesBuffer> _buffers = [];
    private long _bufferedBytes;

    /// <summary>Series that had at least one numeric field, in order of appearance.</summary>
    public IReadOnlyList<SignalSeriesStats> Series => [.. _buffers.Values.Where(b => b.Stats.RecordCount > 0).Select(b => b.Stats)];

    public long ChunkBytes { get; private set; }

    public override void OnSeries(LogSeries series)
    {
        ArgumentNullException.ThrowIfNull(series);
        if (_buffers.ContainsKey(series.Id))
        {
            return;
        }

        var columns = new List<int>();
        var fields = new List<SignalFieldStats>();
        for (var i = 0; i < series.Fields.Count; i++)
        {
            if (series.Fields[i].Kind == LogFieldKind.Numeric)
            {
                fields.Add(new SignalFieldStats(series.Fields[i], columns.Count));
                columns.Add(i);
            }
        }

        if (columns.Count > 0)
        {
            _buffers[series.Id] = new SeriesBuffer(new SignalSeriesStats(series, fields), [.. columns]);
        }
    }

    public override void OnRecord(LogRecord record)
    {
        if (!_buffers.TryGetValue(record.Series.Id, out var buffer))
        {
            return;
        }

        buffer.Add(record);
        _bufferedBytes += buffer.BytesPerRecord;
        if (buffer.Count >= MaxRecordsPerChunk || buffer.Count * buffer.BytesPerRecord >= MaxBytesPerSeries)
        {
            Flush(buffer);
        }
        else if (_bufferedBytes >= MaxBufferedBytes)
        {
            FlushAll();
        }
    }

    /// <summary>Emits the remaining partial chunks. Call once after the parser returned.</summary>
    public void Complete() => FlushAll();

    private void FlushAll()
    {
        foreach (var buffer in _buffers.Values)
        {
            Flush(buffer);
        }
    }

    private void Flush(SeriesBuffer buffer)
    {
        if (buffer.Count == 0)
        {
            return;
        }

        var timestamps = buffer.Timestamps.AsSpan(0, buffer.Count);
        var data = SignalChunkCodec.Encode(timestamps, buffer.Columns);
        long start = long.MaxValue, end = long.MinValue;
        foreach (var t in timestamps)
        {
            start = Math.Min(start, t);
            end = Math.Max(end, t);
        }

        var chunk = new SignalChunk(buffer.Stats.Series.Id, buffer.Stats.ChunkCount, start, end, buffer.Count, data);
        buffer.Stats.ChunkCount++;
        ChunkBytes += data.Length;
        _bufferedBytes -= (long)buffer.Count * buffer.BytesPerRecord;
        buffer.Reset();
        _output(chunk);
    }

    private sealed class SeriesBuffer
    {
        private readonly int[] _fieldIndices;
        private double[][] _columns;

        public SeriesBuffer(SignalSeriesStats stats, int[] fieldIndices)
        {
            Stats = stats;
            _fieldIndices = fieldIndices;
            Timestamps = new long[64];
            _columns = [.. fieldIndices.Select(_ => new double[64])];
            BytesPerRecord = 8L * (fieldIndices.Length + 1);
        }

        public SignalSeriesStats Stats { get; }

        public long[] Timestamps { get; private set; }

        public int Count { get; private set; }

        public long BytesPerRecord { get; }

        /// <summary>Columns trimmed to <see cref="Count"/> for encoding.</summary>
        public IReadOnlyList<double[]> Columns => _columns;

        public void Add(LogRecord record)
        {
            if (Count == Timestamps.Length)
            {
                var size = Math.Min(Count * 2, MaxRecordsPerChunk);
                Timestamps = Grow(Timestamps, size);
                _columns = [.. _columns.Select(c => Grow(c, size))];
            }

            Timestamps[Count] = record.TimestampUs;
            var stats = Stats;
            stats.RecordCount++;
            stats.FirstTimestampUs = Math.Min(stats.FirstTimestampUs ?? long.MaxValue, record.TimestampUs);
            stats.LastTimestampUs = Math.Max(stats.LastTimestampUs ?? long.MinValue, record.TimestampUs);

            for (var c = 0; c < _fieldIndices.Length; c++)
            {
                var value = record.Values[_fieldIndices[c]];
                _columns[c][Count] = value;
                var field = stats.Fields[c];
                field.SampleCount++;
                if (!double.IsFinite(value))
                {
                    field.InvalidCount++;
                }
                else
                {
                    field.Min = field.Min is { } min ? Math.Min(min, value) : value;
                    field.Max = field.Max is { } max ? Math.Max(max, value) : value;
                }
            }

            Count++;
        }

        public void Reset() => Count = 0;

        private static T[] Grow<T>(T[] array, int size)
        {
            var result = new T[size];
            Array.Copy(array, result, array.Length);
            return result;
        }
    }
}
