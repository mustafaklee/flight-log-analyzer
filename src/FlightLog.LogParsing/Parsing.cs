namespace FlightLog.LogParsing;

/// <summary>Resource limits for untrusted input. Exceeding one stops parsing with a <see cref="LogParseException"/>.</summary>
public sealed record ParserLimits
{
    public int MaxFormats { get; init; } = 4096;

    public int MaxSeries { get; init; } = 4096;

    public int MaxFieldsPerSeries { get; init; } = 2048;

    public int MaxNestingDepth { get; init; } = 8;

    public int MaxWarnings { get; init; } = 100;

    /// <summary>Wall-clock limit for one file.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(15);
}

/// <summary>The file cannot be parsed. <see cref="Code"/> is stable and safe to show to users.</summary>
public sealed class LogParseException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>What the parser saw, independent of the sink.</summary>
public sealed class ParserReport
{
    public required string Parser { get; init; }

    public required string ParserVersion { get; init; }

    /// <summary>ULog header timestamp (µs since boot); null for DataFlash.</summary>
    public long? HeaderTimestampUs { get; set; }

    public long MessageCount { get; set; }

    public long RecordCount { get; set; }

    public long? FirstTimestampUs { get; set; }

    public long? LastTimestampUs { get; set; }

    /// <summary>Bytes skipped while resynchronizing after corrupt or unknown data.</summary>
    public long SkippedBytes { get; set; }

    /// <summary>The file ended in the middle of a message.</summary>
    public bool Truncated { get; set; }

    /// <summary>Corrupt data was detected and skipped; the result may be incomplete.</summary>
    public bool Corrupt { get; set; }

    public int SeriesCount { get; set; }

    public List<string> Warnings { get; } = [];

    internal void Warn(ParserLimits limits, string warning)
    {
        if (Warnings.Count < limits.MaxWarnings)
        {
            Warnings.Add(warning);
        }
    }

    internal void ObserveTimestamp(long timestampUs)
    {
        if (FirstTimestampUs is null || timestampUs < FirstTimestampUs)
        {
            FirstTimestampUs = timestampUs;
        }

        if (LastTimestampUs is null || timestampUs > LastTimestampUs)
        {
            LastTimestampUs = timestampUs;
        }
    }
}

public interface IFlightLogParser
{
    string Name { get; }

    string Version { get; }

    /// <summary>Decodes the whole log into <paramref name="sink"/>. The stream must be readable and seekable.</summary>
    ParserReport Parse(Stream input, ILogRecordSink sink, ParserLimits limits, CancellationToken cancellationToken);
}
