using FlightLog.Domain.Common;

namespace FlightLog.Domain.FlightData;

public enum ProcessingJobOutcome
{
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    Cancelled = 4,
}

/// <summary>Counters of one successful processing run.</summary>
public sealed record ProcessingStatistics
{
    public string ParserName { get; init; } = "";

    public string ParserVersion { get; init; } = "";

    public string NormalizationVersion { get; init; } = "";

    public long MessageCount { get; init; }

    public long RecordCount { get; init; }

    public int SeriesCount { get; init; }

    public int SignalCount { get; init; }

    public int EventCount { get; init; }

    public int ParameterCount { get; init; }

    public int ChunkCount { get; init; }

    public long ChunkBytes { get; init; }

    public long SkippedBytes { get; init; }

    /// <summary>The file ends in the middle of a message (common when power is cut).</summary>
    public bool Truncated { get; init; }

    /// <summary>Damaged regions were skipped.</summary>
    public bool Corrupt { get; init; }

    /// <summary>Events or parameters beyond the limits were dropped.</summary>
    public bool SummaryTruncated { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// One attempt to process a log file: who ran it, how long it took, what the parser reported and how it ended.
/// The log file's <c>Status</c> is the user-facing state; jobs are its history.
/// </summary>
public sealed class LogProcessingJob
{
    public const int MaxWarnings = 100;
    public const int FailureMessageMaxLength = 1000;

    private LogProcessingJob()
    {
        WorkerId = null!;
        CorrelationId = null!;
        StoragePrefix = null!;
    }

    public Guid Id { get; private set; }

    public Guid TeamId { get; private set; }

    public Guid LogFileId { get; private set; }

    public Guid FlightId { get; private set; }

    /// <summary>1 for the first attempt on the file, then counting up with each retry.</summary>
    public int Attempt { get; private set; }

    public string WorkerId { get; private set; }

    /// <summary>Ties log lines of this run together.</summary>
    public string CorrelationId { get; private set; }

    /// <summary>Object storage prefix of the chunks this run writes.</summary>
    public string StoragePrefix { get; private set; }

    public ProcessingJobOutcome Outcome { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }

    /// <summary>How long the file waited in the queue before this attempt started.</summary>
    public long QueueWaitMs { get; private set; }

    public DateTimeOffset? FinishedAt { get; private set; }

    public long? DurationMs { get; private set; }

    /// <summary>Managed memory allocated while the job ran (process-wide; the worker runs one job at a time).</summary>
    public long? AllocatedBytes { get; private set; }

    public string? ParserName { get; private set; }

    public string? ParserVersion { get; private set; }

    public string? NormalizationVersion { get; private set; }

    public long MessageCount { get; private set; }

    public long RecordCount { get; private set; }

    public int SeriesCount { get; private set; }

    public int SignalCount { get; private set; }

    public int EventCount { get; private set; }

    public int ParameterCount { get; private set; }

    public int ChunkCount { get; private set; }

    public long ChunkBytes { get; private set; }

    public long SkippedBytes { get; private set; }

    public bool Truncated { get; private set; }

    public bool Corrupt { get; private set; }

    public bool SummaryTruncated { get; private set; }

    public List<string> Warnings { get; private set; } = [];

    public string? FailureCode { get; private set; }

    public string? FailureMessage { get; private set; }

    public static LogProcessingJob Start(
        Guid teamId, Guid logFileId, Guid flightId, int attempt, string workerId, string correlationId, DateTimeOffset queuedSince, DateTimeOffset now)
    {
        if (attempt < 1)
        {
            throw new DomainRuleViolationException("processing_job.invalid_attempt", "Attempts are counted from 1.");
        }

        var id = Guid.CreateVersion7(now);
        return new LogProcessingJob
        {
            Id = id,
            TeamId = Guard.NotEmpty(teamId, "teamId"),
            LogFileId = Guard.NotEmpty(logFileId, "logFileId"),
            FlightId = Guard.NotEmpty(flightId, "flightId"),
            Attempt = attempt,
            WorkerId = Guard.RequiredText(workerId, 200, "workerId"),
            CorrelationId = Guard.RequiredText(correlationId, 100, "correlationId"),
            StoragePrefix = StoragePrefixFor(flightId, id),
            Outcome = ProcessingJobOutcome.Running,
            StartedAt = now,
            QueueWaitMs = (long)Math.Max(0, (now - queuedSince).TotalMilliseconds),
        };
    }

    /// <summary>Derived objects of a flight live under <c>derived/{flight}/</c>, one folder per job.</summary>
    public static string StoragePrefixFor(Guid flightId, Guid jobId) => $"derived/{flightId:N}/{jobId:N}/";

    public void Succeed(ProcessingStatistics statistics, long allocatedBytes, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(statistics);
        Finish(ProcessingJobOutcome.Succeeded, allocatedBytes, now);
        ParserName = statistics.ParserName;
        ParserVersion = statistics.ParserVersion;
        NormalizationVersion = statistics.NormalizationVersion;
        MessageCount = statistics.MessageCount;
        RecordCount = statistics.RecordCount;
        SeriesCount = statistics.SeriesCount;
        SignalCount = statistics.SignalCount;
        EventCount = statistics.EventCount;
        ParameterCount = statistics.ParameterCount;
        ChunkCount = statistics.ChunkCount;
        ChunkBytes = statistics.ChunkBytes;
        SkippedBytes = statistics.SkippedBytes;
        Truncated = statistics.Truncated;
        Corrupt = statistics.Corrupt;
        SummaryTruncated = statistics.SummaryTruncated;
        Warnings = [.. statistics.Warnings.Take(MaxWarnings).Select(w => w.Length > 500 ? w[..500] : w)];
    }

    public void Fail(string code, string message, long allocatedBytes, DateTimeOffset now)
    {
        Finish(ProcessingJobOutcome.Failed, allocatedBytes, now);
        FailureCode = Guard.RequiredText(code, 100, "failureCode");
        var text = Guard.RequiredText(message, int.MaxValue, "failureMessage");
        FailureMessage = text.Length > FailureMessageMaxLength ? text[..FailureMessageMaxLength] : text;
    }

    public void Cancel(long allocatedBytes, DateTimeOffset now) => Finish(ProcessingJobOutcome.Cancelled, allocatedBytes, now);

    private void Finish(ProcessingJobOutcome outcome, long allocatedBytes, DateTimeOffset now)
    {
        if (Outcome != ProcessingJobOutcome.Running)
        {
            throw new DomainRuleViolationException("processing_job.already_finished", $"The job already ended as {Outcome}.");
        }

        Outcome = outcome;
        FinishedAt = now;
        DurationMs = (long)Math.Max(0, (now - StartedAt).TotalMilliseconds);
        AllocatedBytes = Math.Max(0, allocatedBytes);
    }
}
