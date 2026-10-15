using FlightLog.Domain.Common;

namespace FlightLog.Domain.Analysis;

public enum AiAnalysisStatus
{
    Queued = 1,
    Running = 2,
    Succeeded = 3,
    Failed = 4,
}

/// <summary>Raised when an AI analysis enters the queue; <see cref="NotBefore"/> delays an automatic retry.</summary>
public sealed record AiAnalysisQueued(Guid AiAnalysisId, Guid FlightId, Guid TeamId, DateTimeOffset? NotBefore) : IDomainEvent;

/// <summary>Token counts of one or more model calls.</summary>
public sealed record AiUsage(long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens)
{
    public static readonly AiUsage None = new(0, 0, 0, 0);

    public AiUsage Add(AiUsage other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new(InputTokens + other.InputTokens, OutputTokens + other.OutputTokens, CacheReadTokens + other.CacheReadTokens, CacheWriteTokens + other.CacheWriteTokens);
    }
}

/// <summary>What was sent to the model and how it was configured: enough to explain and reproduce a report.</summary>
public sealed record AiAnalysisInput(
    string Provider,
    string Model,
    string PromptVersion,
    string ContextVersion,
    string ContextHash,
    string Context,
    IReadOnlyDictionary<string, string> ContextSummary);

/// <summary>
/// A model-written report about a flight, based on one succeeded rule analysis (<see cref="AnalysisRunId"/>).
/// It never changes the rule findings; it is a separate, versioned interpretation of them. Every model call is
/// accounted for (tokens, cost, latency) whether the analysis succeeds or not.
/// </summary>
public sealed class AiAnalysis : IHasDomainEvents
{
    public const int FailureMessageMaxLength = 1000;
    public const int RawOutputMaxLength = 200_000;

    private readonly List<IDomainEvent> _domainEvents = [];

    private AiAnalysis()
    {
        CorrelationId = null!;
        Provider = null!;
        Model = null!;
        PromptVersion = null!;
    }

    public Guid Id { get; private set; }

    public Guid TeamId { get; private set; }

    public Guid FlightId { get; private set; }

    /// <summary>The rule analysis whose findings the report interprets.</summary>
    public Guid AnalysisRunId { get; private set; }

    public Guid RequestedBy { get; private set; }

    public AiAnalysisStatus Status { get; private set; }

    /// <summary>Provider and model as configured when the analysis was queued.</summary>
    public string Provider { get; private set; }

    public string Model { get; private set; }

    /// <summary>The model the provider reports it used (may carry a snapshot suffix).</summary>
    public string? ResponseModel { get; private set; }

    public string PromptVersion { get; private set; }

    public string? ContextVersion { get; private set; }

    /// <summary>SHA-256 of <see cref="Context"/>: equal hashes mean the model saw the same input.</summary>
    public string? ContextHash { get; private set; }

    /// <summary>The exact context sent to the model (findings, statistics, events; never the raw log).</summary>
    public string? Context { get; private set; }

    /// <summary>What the context contains and what was left out (counts), for the report's limitations.</summary>
    public IReadOnlyDictionary<string, string> ContextSummary { get; private set; } = new Dictionary<string, string>();

    public string CorrelationId { get; private set; }

    public int Attempt { get; private set; }

    public int RetryCount { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? NextAttemptAt { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? FinishedAt { get; private set; }

    /// <summary>Wall time of the last attempt, context preparation included.</summary>
    public long? DurationMs { get; private set; }

    public string? WorkerId { get; private set; }

    /// <summary>Model calls made over all attempts (including repair and failed calls that returned usage).</summary>
    public int ProviderCalls { get; private set; }

    /// <summary>Time spent waiting for the provider over all calls.</summary>
    public long LatencyMs { get; private set; }

    public long InputTokens { get; private set; }

    public long OutputTokens { get; private set; }

    public long CacheReadTokens { get; private set; }

    public long CacheWriteTokens { get; private set; }

    /// <summary>Estimated cost in USD from the configured price list; null when the model has no price entry.</summary>
    public decimal? CostUsd { get; private set; }

    /// <summary>Provider stop reason of the last call (end_turn, max_tokens, refusal...).</summary>
    public string? StopReason { get; private set; }

    /// <summary>The model's answer as received (JSON), kept for audit.</summary>
    public string? RawOutput { get; private set; }

    /// <summary>The validated report (JSON): evidence resolved, unsupported claims and unverified numbers marked.</summary>
    public string? Report { get; private set; }

    public string? FailureCode { get; private set; }

    public string? FailureMessage { get; private set; }

    public uint Version { get; private set; }

    public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents;

    public void ClearDomainEvents() => _domainEvents.Clear();

    public static AiAnalysis Queue(
        Guid teamId, Guid flightId, Guid analysisRunId, Guid requestedBy, string provider, string model, string promptVersion, DateTimeOffset now)
    {
        var analysis = new AiAnalysis
        {
            Id = Guid.CreateVersion7(now),
            TeamId = Guard.NotEmpty(teamId, "teamId"),
            FlightId = Guard.NotEmpty(flightId, "flightId"),
            AnalysisRunId = Guard.NotEmpty(analysisRunId, "analysisRunId"),
            RequestedBy = Guard.NotEmpty(requestedBy, "requestedBy"),
            Status = AiAnalysisStatus.Queued,
            Provider = Guard.RequiredText(provider, 50, "provider"),
            Model = Guard.RequiredText(model, 100, "model"),
            PromptVersion = Guard.RequiredText(promptVersion, 50, "promptVersion"),
            CorrelationId = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
        };
        analysis._domainEvents.Add(new AiAnalysisQueued(analysis.Id, flightId, teamId, null));
        return analysis;
    }

    public void Start(string workerId, DateTimeOffset now)
    {
        EnsureStatus(AiAnalysisStatus.Queued);
        Status = AiAnalysisStatus.Running;
        WorkerId = Guard.RequiredText(workerId, 200, "workerId");
        Attempt++;
        StartedAt = now;
        NextAttemptAt = null;
    }

    /// <summary>Records the input of the current attempt (the context is rebuilt on every attempt).</summary>
    public void RecordInput(AiAnalysisInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        EnsureStatus(AiAnalysisStatus.Running);
        Provider = Guard.RequiredText(input.Provider, 50, "provider");
        Model = Guard.RequiredText(input.Model, 100, "model");
        PromptVersion = Guard.RequiredText(input.PromptVersion, 50, "promptVersion");
        ContextVersion = Guard.RequiredText(input.ContextVersion, 50, "contextVersion");
        ContextHash = Guard.RequiredText(input.ContextHash, 64, "contextHash");
        Context = input.Context ?? throw new ArgumentNullException(nameof(input));
        ContextSummary = new Dictionary<string, string>(input.ContextSummary, StringComparer.Ordinal);
    }

    /// <summary>Adds one model call. Also for failed calls: tokens that were billed stay accounted for.</summary>
    public void RecordCall(AiUsage usage, long latencyMs, decimal? costUsd, string? responseModel, string? stopReason)
    {
        ArgumentNullException.ThrowIfNull(usage);
        ProviderCalls++;
        LatencyMs += Math.Max(0, latencyMs);
        InputTokens += usage.InputTokens;
        OutputTokens += usage.OutputTokens;
        CacheReadTokens += usage.CacheReadTokens;
        CacheWriteTokens += usage.CacheWriteTokens;
        if (costUsd is { } cost)
        {
            CostUsd = (CostUsd ?? 0m) + cost;
        }

        ResponseModel = Guard.OptionalText(responseModel, 100, "responseModel") ?? ResponseModel;
        StopReason = Guard.OptionalText(stopReason, 50, "stopReason") ?? StopReason;
    }

    public void Succeed(string rawOutput, string report, DateTimeOffset now)
    {
        EnsureStatus(AiAnalysisStatus.Running);
        RawOutput = Truncate(Guard.RequiredText(rawOutput, int.MaxValue, "rawOutput"), RawOutputMaxLength);
        Report = Guard.RequiredText(report, int.MaxValue, "report");
        Finish(AiAnalysisStatus.Succeeded, now);
    }

    /// <summary>Ends the analysis without report. Allowed for queued and running analyses.</summary>
    public void Fail(string code, string message, string? rawOutput, DateTimeOffset now)
    {
        if (Status is not (AiAnalysisStatus.Queued or AiAnalysisStatus.Running))
        {
            throw new DomainRuleViolationException("ai_analysis.already_finished", $"The AI analysis already ended as {Status}.");
        }

        Finish(AiAnalysisStatus.Failed, now);
        FailureCode = Guard.RequiredText(code, 100, "failureCode");
        FailureMessage = Truncate(Guard.RequiredText(message, int.MaxValue, "failureMessage"), FailureMessageMaxLength);
        if (!string.IsNullOrEmpty(rawOutput))
        {
            RawOutput = Truncate(rawOutput, RawOutputMaxLength);
        }
    }

    /// <summary>Back to the queue: retry after a transient failure, or worker shutdown.</summary>
    public void Requeue(DateTimeOffset? notBefore, bool countsAsRetry)
    {
        EnsureStatus(AiAnalysisStatus.Running);
        Status = AiAnalysisStatus.Queued;
        StartedAt = null;
        NextAttemptAt = notBefore;
        if (countsAsRetry)
        {
            RetryCount++;
        }

        _domainEvents.Add(new AiAnalysisQueued(Id, FlightId, TeamId, notBefore));
    }

    private static string Truncate(string text, int max) => text.Length > max ? text[..max] : text;

    private void Finish(AiAnalysisStatus status, DateTimeOffset now)
    {
        Status = status;
        FinishedAt = now;
        DurationMs = StartedAt is { } started ? (long)Math.Max(0, (now - started).TotalMilliseconds) : null;
    }

    private void EnsureStatus(AiAnalysisStatus expected)
    {
        if (Status != expected)
        {
            throw new DomainRuleViolationException("ai_analysis.invalid_status", $"The AI analysis is {Status}, expected {expected}.");
        }
    }
}
