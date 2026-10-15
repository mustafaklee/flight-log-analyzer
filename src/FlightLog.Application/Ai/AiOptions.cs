namespace FlightLog.Application.Ai;

/// <summary>Price of a model in USD per million tokens.</summary>
public sealed class AiModelPrice
{
    public decimal InputPerMillion { get; set; }

    public decimal OutputPerMillion { get; set; }

    public decimal CacheReadPerMillion { get; set; }

    public decimal CacheWritePerMillion { get; set; }
}

/// <summary>AI analysis settings (section <c>Ai</c>, ADR-0008). The API key is only given to the worker.</summary>
public sealed class AiOptions
{
    public const string SectionName = "Ai";

    /// <summary>Whether users can request AI reports. Off by default: it sends flight data to an external provider.</summary>
    public bool Enabled { get; set; }

    /// <summary>Provider adapter: <c>Anthropic</c>.</summary>
    public string Provider { get; set; } = "Anthropic";

    public string Model { get; set; } = "claude-opus-5-5";

    /// <summary>Reasoning effort passed to the model: low, medium, high, xhigh, max.</summary>
    public string Effort { get; set; } = "high";

    /// <summary>Provider API key (worker only; from the environment, never from a committed file).</summary>
    public string? ApiKey { get; set; }

    /// <summary>Alternative API endpoint (gateway); the provider default when empty.</summary>
    public string? BaseUrl { get; set; }

    public int MaxOutputTokens { get; set; } = 16_000;

    /// <summary>Upper bound of one model call.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Immediate retries of one call inside the provider client (429, 5xx, connection errors).</summary>
    public int ClientMaxRetries { get; set; } = 2;

    /// <summary>Automatic re-queues after a transient provider failure, on top of the client retries.</summary>
    public int MaxAutomaticRetries { get; set; } = 2;

    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromMinutes(1);

    public TimeSpan RetryMaxDelay { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>An analysis left running longer than this (crashed worker) is retried or failed.</summary>
    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>AI analyses a team may request per UTC day; 0 means unlimited.</summary>
    public int DailyLimitPerTeam { get; set; } = 20;

    /// <summary>Upper bound of the context sent to the model; lower-priority items are left out first.</summary>
    public int MaxContextCharacters { get; set; } = 60_000;

    public int MaxFindings { get; set; } = 60;

    public int MaxEvents { get; set; } = 80;

    public int MaxParameterChanges { get; set; } = 40;

    public int MaxSignalStatistics { get; set; } = 60;

    /// <summary>Prices per model id (USD per million tokens). A model without entry is reported without cost.</summary>
    public Dictionary<string, AiModelPrice> Prices { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        // List prices as of 2026-10; override in configuration when they change.
        ["claude-opus-5-5"] = new() { InputPerMillion = 4m, OutputPerMillion = 20m, CacheReadPerMillion = 0.20m, CacheWritePerMillion = 5m },
        ["claude-opus-5"] = new() { InputPerMillion = 5m, OutputPerMillion = 25m, CacheReadPerMillion = 0.50m, CacheWritePerMillion = 6.25m },
        ["claude-sonnet-5-5"] = new() { InputPerMillion = 2m, OutputPerMillion = 10m, CacheReadPerMillion = 0.20m, CacheWritePerMillion = 2.50m },
        ["claude-haiku-5-5"] = new() { InputPerMillion = 0.10m, OutputPerMillion = 0.50m, CacheReadPerMillion = 0.01m, CacheWritePerMillion = 0.125m },
    };

    /// <summary>Delay before automatic retry number <paramref name="retry"/> (1 for the first).</summary>
    public TimeSpan RetryDelay(int retry)
    {
        var factor = Math.Pow(2, Math.Clamp(retry - 1, 0, 20));
        var delay = TimeSpan.FromTicks((long)Math.Min(RetryBaseDelay.Ticks * factor, RetryMaxDelay.Ticks));
        return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
    }

    /// <summary>Estimated cost of a call, or null when the model has no price entry.</summary>
    public decimal? Cost(string model, Domain.Analysis.AiUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        if (!Prices.TryGetValue(model, out var price))
        {
            return null;
        }

        const decimal Million = 1_000_000m;
        return Math.Round(
            ((usage.InputTokens * price.InputPerMillion) + (usage.OutputTokens * price.OutputPerMillion)
                + (usage.CacheReadTokens * price.CacheReadPerMillion) + (usage.CacheWriteTokens * price.CacheWritePerMillion)) / Million,
            6);
    }
}
