using System.Text.Json;
using FlightLog.Domain.Analysis;

namespace FlightLog.Application.Ai;

/// <summary>One model call: instructions, the flight context and the JSON schema the answer must follow.</summary>
public sealed record AiProviderRequest(string SystemPrompt, string UserMessage, JsonElement OutputSchema, int MaxOutputTokens);

/// <summary>The model's answer and what the call cost.</summary>
public sealed record AiProviderResponse(string? Text, string? StopReason, string? Model, AiUsage Usage, long LatencyMs, string? RefusalExplanation = null);

/// <summary>Why a model call failed, as far as retrying is concerned.</summary>
public enum AiProviderErrorKind
{
    /// <summary>Overloaded, 5xx, connection or timeout: worth trying again later.</summary>
    Transient = 1,

    /// <summary>429: try again later, after <see cref="AiProviderException.RetryAfter"/> when given.</summary>
    RateLimited = 2,

    /// <summary>The key is missing, invalid or lacks permission. Retrying does not help.</summary>
    Authentication = 3,

    /// <summary>The provider rejected the request (invalid request, model unknown, no credit). Retrying does not help.</summary>
    Rejected = 4,
}

public sealed class AiProviderException(AiProviderErrorKind kind, string message, TimeSpan? retryAfter = null, Exception? inner = null)
    : Exception(message, inner)
{
    public AiProviderErrorKind Kind { get; } = kind;

    public TimeSpan? RetryAfter { get; } = retryAfter;

    public bool IsTransient => Kind is AiProviderErrorKind.Transient or AiProviderErrorKind.RateLimited;
}

/// <summary>
/// A language-model provider that writes a flight report (ADR-0008). Adapters live in the infrastructure layer;
/// they must not log prompts, answers or keys.
/// </summary>
public interface IFlightAnalysisProvider
{
    /// <summary>Provider name stored with each analysis (for example <c>Anthropic</c>).</summary>
    string Name { get; }

    /// <summary>The configured model id.</summary>
    string Model { get; }

    /// <summary>False when the provider cannot be called (no key); analyses then fail with <c>ai.not_configured</c>.</summary>
    bool IsConfigured { get; }

    Task<AiProviderResponse> CompleteAsync(AiProviderRequest request, CancellationToken cancellationToken);
}
