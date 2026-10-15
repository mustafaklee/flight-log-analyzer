namespace FlightLog.Application.Processing;

public sealed class ProcessingOptions
{
    public const string SectionName = "Processing";

    /// <summary>Runs the processing loop inside this process (the API in development, always in the worker).</summary>
    public bool Enabled { get; set; }

    /// <summary>How often the database is checked for queued files when there was nothing to do.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How often a running job checks whether its file was cancelled.</summary>
    public TimeSpan CancellationCheckInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Upper bound of parse time for one file; the parser stops with <c>parser.timeout</c>.</summary>
    public TimeSpan ParserTimeout { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>A file left in Processing for longer than this (crashed worker) is marked as failed.</summary>
    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Directory for the downloaded log and the chunks before upload; system temp when empty.</summary>
    public string? TempDirectory { get; set; }

    public int MaxParallelUploads { get; set; } = 8;

    /// <summary>Automatic retries after transient failures (storage or database unavailable, worker crash).</summary>
    public int MaxAutomaticRetries { get; set; } = 3;

    /// <summary>Delay before the first automatic retry; it doubles with each further retry.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan RetryMaxDelay { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>An analysis left running for longer than this (crashed worker) is retried or failed.</summary>
    public TimeSpan AnalysisStaleAfter { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Delay before automatic retry number <paramref name="retry"/> (1 for the first).</summary>
    public TimeSpan RetryDelay(int retry)
    {
        var factor = Math.Pow(2, Math.Clamp(retry - 1, 0, 20));
        var delay = TimeSpan.FromTicks((long)Math.Min(RetryBaseDelay.Ticks * factor, RetryMaxDelay.Ticks));
        return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
    }
}
