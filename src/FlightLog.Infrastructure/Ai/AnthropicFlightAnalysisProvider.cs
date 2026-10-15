using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Core;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using FlightLog.Application.Ai;
using FlightLog.Domain.Analysis;
using Microsoft.Extensions.Options;

namespace FlightLog.Infrastructure.Ai;

/// <summary>
/// Claude through the Anthropic Messages API (official .NET SDK, MIT). The answer is constrained to the report's
/// JSON schema with structured outputs (<c>output_config.format</c>); thinking depth is set with
/// <c>output_config.effort</c>. Neither the prompt nor the answer nor the key is logged.
/// </summary>
internal sealed class AnthropicFlightAnalysisProvider : IFlightAnalysisProvider, IDisposable
{
    private const int MaxErrorTextLength = 300;

    private readonly AiOptions _options;
    private readonly AnthropicClient? _client;

    public AnthropicFlightAnalysisProvider(IOptions<AiOptions> options, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            _client = new AnthropicClient
            {
                ApiKey = _options.ApiKey,
                MaxRetries = Math.Max(0, _options.ClientMaxRetries),
                Timeout = _options.RequestTimeout,
            };
            if (httpClient is not null || !string.IsNullOrWhiteSpace(_options.BaseUrl))
            {
                _client = (AnthropicClient)_client.WithOptions(o => o with
                {
                    HttpClient = httpClient ?? o.HttpClient,
                    BaseUrl = string.IsNullOrWhiteSpace(_options.BaseUrl) ? o.BaseUrl : _options.BaseUrl,
                });
            }
        }
    }

    public string Name => "Anthropic";

    public string Model => _options.Model;

    public bool IsConfigured => _client is not null;

    public async Task<AiProviderResponse> CompleteAsync(AiProviderRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_client is null)
        {
            throw new AiProviderException(AiProviderErrorKind.Authentication, "No API key is configured.");
        }

        if (request.OutputSchema.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("The output schema must be a JSON object.", nameof(request));
        }

        var schema = request.OutputSchema.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
        var parameters = new MessageCreateParams
        {
            Model = _options.Model,
            MaxTokens = request.MaxOutputTokens,
            System = request.SystemPrompt,
            Messages = [new() { Role = Role.User, Content = request.UserMessage }],
            OutputConfig = new OutputConfig
            {
                Effort = ParseEffort(_options.Effort),
                Format = new JsonOutputFormat { Schema = schema },
            },
        };

        var started = Stopwatch.GetTimestamp();
        Message message;
        try
        {
            message = await _client.Messages.Create(parameters, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw Map(ex);
        }

        var latency = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var text = new StringBuilder();
        foreach (var block in message.Content)
        {
            if (block.TryPickText(out var textBlock))
            {
                text.Append(textBlock.Text);
            }
        }

        var usage = message.Usage;
        return new AiProviderResponse(
            text.Length > 0 ? text.ToString() : null,
            message.StopReason?.Raw(),
            message.Model.Raw(),
            new AiUsage(usage.InputTokens, usage.OutputTokens, usage.CacheReadInputTokens ?? 0, usage.CacheCreationInputTokens ?? 0),
            latency,
            message.StopDetails?.Explanation);
    }

    public void Dispose() => _client?.Dispose();

    /// <summary>Unknown effort names fall back to the provider default rather than failing every request.</summary>
    internal static Effort ParseEffort(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "low" => Effort.Low,
        "medium" => Effort.Medium,
        "xhigh" => Effort.Xhigh,
        "max" => Effort.Max,
        _ => Effort.High,
    };

    internal static AiProviderException Map(Exception ex) => ex switch
    {
        AnthropicUnauthorizedException or AnthropicForbiddenException =>
            new(AiProviderErrorKind.Authentication, "The provider rejected the credentials.", null, ex),
        AnthropicRateLimitException =>
            new(AiProviderErrorKind.RateLimited, "Rate limit reached.", null, ex),
        Anthropic5xxException =>
            new(AiProviderErrorKind.Transient, "The provider returned a server error.", null, ex),
        AnthropicApiException { StatusCode: HttpStatusCode.RequestTimeout or HttpStatusCode.Conflict } or AnthropicApiException { StatusCode: >= (HttpStatusCode)500 } =>
            new(AiProviderErrorKind.Transient, "The provider is overloaded or unavailable.", null, ex),
        AnthropicApiException api =>
            new(AiProviderErrorKind.Rejected, ErrorText(api), null, ex),
        AnthropicIOException or AnthropicInvalidDataException or HttpRequestException or TimeoutException or OperationCanceledException =>
            new(AiProviderErrorKind.Transient, "The provider could not be reached or did not answer in time.", null, ex),
        _ => new(AiProviderErrorKind.Transient, "The provider call failed.", null, ex),
    };

    /// <summary>The API's own error message (status and text), without request content.</summary>
    private static string ErrorText(AnthropicApiException ex)
    {
        var detail = "";
        try
        {
            using var body = JsonDocument.Parse(ex.ResponseBody ?? "");
            if (body.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("message", out var msg))
            {
                detail = msg.GetString() ?? "";
            }
        }
        catch (JsonException)
        {
        }

        var text = $"HTTP {(int)ex.StatusCode}{(detail.Length > 0 ? $": {detail}" : "")}";
        return text.Length > MaxErrorTextLength ? text[..MaxErrorTextLength] : text;
    }
}

/// <summary>Used when AI analysis is configured with a provider this build does not know.</summary>
internal sealed class UnknownFlightAnalysisProvider(string name, string model) : IFlightAnalysisProvider
{
    public string Name => name;

    public string Model => model;

    public bool IsConfigured => false;

    public Task<AiProviderResponse> CompleteAsync(AiProviderRequest request, CancellationToken cancellationToken) =>
        throw new AiProviderException(AiProviderErrorKind.Rejected, $"Unknown AI provider '{name}'.");
}
