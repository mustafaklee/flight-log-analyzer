using System.Diagnostics;
using System.Text.Json;
using FlightLog.Application.Abstractions;
using FlightLog.Application.Processing;
using FlightLog.Domain.Analysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlightLog.Application.Ai;

/// <summary>An AI analysis this worker has moved to Running.</summary>
public sealed record ClaimedAiAnalysis(Guid Id, Guid FlightId, Guid AnalysisRunId, string CorrelationId);

/// <summary>Executes queued AI analyses (worker side).</summary>
public interface IAiAnalysisService
{
    /// <summary>Claims the oldest queued analysis that is due (database transport); null when none is queued.</summary>
    Task<ClaimedAiAnalysis?> ClaimNextAsync(string workerId, CancellationToken cancellationToken);

    /// <summary>Claims the given analysis; null when it is not queued (anymore) or another worker took it.</summary>
    Task<ClaimedAiAnalysis?> ClaimAsync(Guid id, string workerId, CancellationToken cancellationToken);

    /// <summary>Builds the context, calls the model, validates the answer and stores the report or the failure.</summary>
    Task<WorkOutcome> RunAsync(ClaimedAiAnalysis claim, CancellationToken shutdown);

    /// <summary>Retries (or fails) analyses left running by a worker that died.</summary>
    Task<int> RecoverStaleAsync(CancellationToken cancellationToken);
}

internal sealed partial class AiAnalysisService(
    IApplicationDbContext db,
    IFileStorage storage,
    IFlightAnalysisProvider provider,
    TimeProvider clock,
    ITransientErrorClassifier errors,
    ProcessingMetrics metrics,
    IOptions<AiOptions> options,
    ILogger<AiAnalysisService> logger) : IAiAnalysisService
{
    /// <summary>Further calls after an answer that is not a readable report.</summary>
    private const int MaxRepairCalls = 1;

    public async Task<ClaimedAiAnalysis?> ClaimNextAsync(string workerId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var candidate = await db.AiAnalyses.AsNoTracking()
            .Where(a => a.Status == AiAnalysisStatus.Queued && (a.NextAttemptAt == null || a.NextAttemptAt <= now))
            .OrderBy(a => a.CreatedAt).ThenBy(a => a.Id)
            .Select(a => (Guid?)a.Id)
            .FirstOrDefaultAsync(cancellationToken);
        return candidate is { } id ? await ClaimAsync(id, workerId, cancellationToken) : null;
    }

    public async Task<ClaimedAiAnalysis?> ClaimAsync(Guid id, string workerId, CancellationToken cancellationToken)
    {
        var analysis = await db.AiAnalyses.SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (analysis is null || analysis.Status != AiAnalysisStatus.Queued)
        {
            return null;
        }

        try
        {
            analysis.Start(workerId, clock.GetUtcNow());

            // The row version makes this a compare-and-set.
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return null;
        }

        return new ClaimedAiAnalysis(analysis.Id, analysis.FlightId, analysis.AnalysisRunId, analysis.CorrelationId);
    }

    public async Task<WorkOutcome> RunAsync(ClaimedAiAnalysis claim, CancellationToken shutdown)
    {
        ArgumentNullException.ThrowIfNull(claim);
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = claim.CorrelationId, ["AiAnalysisId"] = claim.Id });
        var started = clock.GetTimestamp();
        WorkOutcome outcome;
        string? code = null;
        try
        {
            (outcome, code) = await ExecuteAsync(claim, shutdown);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            outcome = await EndWithoutResultAsync(claim.Id, WorkOutcome.Requeued, null, null, null);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The flight was deleted while the analysis ran.
            db.ChangeTracker.Clear();
            outcome = WorkOutcome.Skipped;
        }
        catch (AiProviderException ex) when (ex.IsTransient)
        {
            LogProviderFailure(ex.Kind, claim.Id);
            code = ex.Kind == AiProviderErrorKind.RateLimited ? "ai.rate_limited" : "ai.provider_unavailable";
            var message = ex.Kind == AiProviderErrorKind.RateLimited
                ? "The AI provider's rate limit was reached."
                : "The AI provider was unavailable or did not answer in time.";
            outcome = await EndWithoutResultAsync(claim.Id, WorkOutcome.RetryScheduled, code, message, ex.RetryAfter);
        }
        catch (AiProviderException ex)
        {
            LogProviderFailure(ex.Kind, claim.Id);
            (code, var message) = ex.Kind == AiProviderErrorKind.Authentication
                ? ("ai.provider_auth", "The AI provider rejected the API key. An administrator has to check the configuration.")
                : ("ai.provider_rejected", $"The AI provider rejected the request: {ex.Message}");
            outcome = await EndWithoutResultAsync(claim.Id, WorkOutcome.Failed, code, message, null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && errors.IsTransient(ex))
        {
            LogTransientFailure(ex, claim.Id);
            code = "ai.transient_failure";
            outcome = await EndWithoutResultAsync(
                claim.Id, WorkOutcome.RetryScheduled, code, $"The AI analysis was interrupted by a temporary infrastructure problem (correlation id {claim.CorrelationId}).", null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogUnexpectedFailure(ex, claim.Id);
            code = "ai.failed";
            outcome = await EndWithoutResultAsync(
                claim.Id, WorkOutcome.Failed, code, $"The AI analysis failed because of an internal error (correlation id {claim.CorrelationId}).", null);
        }

        metrics.AiAnalysisEnded(outcome, code, clock.GetElapsedTime(started));
        return outcome;
    }

    public async Task<int> RecoverStaleAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var threshold = now - options.Value.StaleAfter;
        var stale = await db.AiAnalyses.Where(a => a.Status == AiAnalysisStatus.Running && a.StartedAt < threshold).ToListAsync(cancellationToken);
        foreach (var analysis in stale)
        {
            Abandon(analysis, "ai.interrupted", "The AI analysis stopped unexpectedly (the worker stopped responding).", null, now);
        }

        if (stale.Count == 0)
        {
            return 0;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return 0;
        }

        LogRecovered(stale.Count);
        return stale.Count;
    }

    private async Task<(WorkOutcome Outcome, string? Code)> ExecuteAsync(ClaimedAiAnalysis claim, CancellationToken cancellationToken)
    {
        var analysis = await db.AiAnalyses.SingleAsync(a => a.Id == claim.Id, cancellationToken);
        if (!provider.IsConfigured)
        {
            return await FailAsync(analysis, "ai.not_configured", "The AI provider is not configured on the worker (no API key).", null, cancellationToken);
        }

        var basis = await db.AnalysisRuns.AsNoTracking().Where(r => r.Id == claim.AnalysisRunId).Select(r => (AnalysisRunStatus?)r.Status).SingleOrDefaultAsync(cancellationToken);
        if (basis != AnalysisRunStatus.Succeeded)
        {
            return await FailAsync(analysis, "ai.rule_analysis_missing", "The rule analysis this report was based on no longer exists.", null, cancellationToken);
        }

        var input = await FlightContextLoader.LoadAsync(db, storage, claim.FlightId, claim.AnalysisRunId, cancellationToken);
        var context = FlightContextBuilder.Build(input, FlightContextLimits.From(options.Value));
        analysis.RecordInput(new AiAnalysisInput(
            provider.Name, provider.Model, FlightReportPrompt.Version, FlightContextBuilder.Version, context.Hash, context.Json, context.Summary));
        await db.SaveChangesAsync(cancellationToken);

        var userMessage = FlightReportPrompt.UserMessage(context);
        for (var call = 0; ; call++)
        {
            var response = await CallAsync(analysis, userMessage, cancellationToken);
            switch (response.StopReason)
            {
                case "refusal":
                    var explanation = string.IsNullOrWhiteSpace(response.RefusalExplanation) ? "" : $" ({response.RefusalExplanation})";
                    return await FailAsync(analysis, "ai.refused", $"The model declined to write this report{explanation}.", response.Text, cancellationToken);
                case "max_tokens":
                    return await FailAsync(
                        analysis, "ai.truncated", $"The answer was cut off at the output limit of {options.Value.MaxOutputTokens} tokens.", response.Text, cancellationToken);
                case "model_context_window_exceeded":
                    return await FailAsync(analysis, "ai.context_too_large", "The flight context did not fit into the model's context window.", response.Text, cancellationToken);
            }

            try
            {
                var report = FlightReportValidator.Validate(response.Text ?? "", context);
                analysis.Succeed(response.Text!, JsonSerializer.Serialize(report, FlightReportValidator.StorageOptions), clock.GetUtcNow());
                await db.SaveChangesAsync(cancellationToken);
                LogSucceeded(analysis.Id, report.Validation.Items, report.Validation.UnsupportedItems, report.Validation.UnknownRefs.Count, analysis.InputTokens, analysis.OutputTokens);
                return (WorkOutcome.Succeeded, null);
            }
            catch (AiReportFormatException ex)
            {
                LogInvalidAnswer(analysis.Id, call + 1);
                if (call >= MaxRepairCalls)
                {
                    return await FailAsync(analysis, "ai.invalid_output", $"The model's answer was not a valid report: {ex.Message}", response.Text, cancellationToken);
                }

                userMessage = FlightReportPrompt.RepairMessage(context, ex.Message);
            }
        }
    }

    /// <summary>One model call; its usage and latency are saved right away, also when the call fails.</summary>
    private async Task<AiProviderResponse> CallAsync(AiAnalysis analysis, string userMessage, CancellationToken cancellationToken)
    {
        var request = new AiProviderRequest(FlightReportPrompt.SystemPrompt, userMessage, FlightReportPrompt.OutputSchema, options.Value.MaxOutputTokens);
        var started = Stopwatch.GetTimestamp();
        AiProviderResponse response;
        try
        {
            response = await provider.CompleteAsync(request, cancellationToken);
        }
        catch (AiProviderException)
        {
            analysis.RecordCall(AiUsage.None, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, null, null, null);
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }

        var cost = options.Value.Cost(provider.Model, response.Usage);
        analysis.RecordCall(response.Usage, response.LatencyMs, cost, response.Model, response.StopReason);
        await db.SaveChangesAsync(CancellationToken.None);
        metrics.AiCall(provider.Model, response.Usage, cost);
        return response;
    }

    private async Task<(WorkOutcome, string?)> FailAsync(AiAnalysis analysis, string code, string message, string? rawOutput, CancellationToken cancellationToken)
    {
        LogFailed(analysis.Id, code);
        analysis.Fail(code, message, rawOutput, clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        return (WorkOutcome.Failed, code);
    }

    private async Task<WorkOutcome> EndWithoutResultAsync(Guid id, WorkOutcome wanted, string? code, string? message, TimeSpan? retryAfter)
    {
        var token = CancellationToken.None;
        db.ChangeTracker.Clear();
        var analysis = await db.AiAnalyses.SingleOrDefaultAsync(a => a.Id == id, token);
        if (analysis is null || analysis.Status != AiAnalysisStatus.Running)
        {
            return WorkOutcome.Skipped;
        }

        var now = clock.GetUtcNow();
        var outcome = wanted switch
        {
            WorkOutcome.Requeued => Requeue(analysis),
            WorkOutcome.RetryScheduled => Abandon(analysis, code!, message!, retryAfter, now),
            _ => Fail(analysis, code!, message!, now),
        };

        try
        {
            await db.SaveChangesAsync(token);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return WorkOutcome.Skipped;
        }

        return outcome;

        static WorkOutcome Requeue(AiAnalysis a)
        {
            a.Requeue(null, countsAsRetry: false);
            return WorkOutcome.Requeued;
        }

        static WorkOutcome Fail(AiAnalysis a, string code, string message, DateTimeOffset now)
        {
            a.Fail(code, message, null, now);
            return WorkOutcome.Failed;
        }
    }

    /// <summary>Retries after a transient failure (not before the provider's retry-after), or fails when retries are used up.</summary>
    private WorkOutcome Abandon(AiAnalysis analysis, string code, string message, TimeSpan? retryAfter, DateTimeOffset now)
    {
        var settings = options.Value;
        if (analysis.RetryCount < settings.MaxAutomaticRetries)
        {
            var delay = settings.RetryDelay(analysis.RetryCount + 1);
            if (retryAfter is { } wait && wait > delay)
            {
                delay = wait;
            }

            analysis.Requeue(now + delay, countsAsRetry: true);
            return WorkOutcome.RetryScheduled;
        }

        analysis.Fail(code, $"{message} Gave up after {analysis.RetryCount} automatic retries.", null, now);
        return WorkOutcome.RetriesExhausted;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "AI analysis {Id} finished: {Items} items, {Unsupported} unsupported, {UnknownRefs} unknown references, {InputTokens} input / {OutputTokens} output tokens")]
    private partial void LogSucceeded(Guid id, int items, int unsupported, int unknownRefs, long inputTokens, long outputTokens);

    [LoggerMessage(Level = LogLevel.Warning, Message = "AI analysis {Id} failed with {Code}")]
    private partial void LogFailed(Guid id, string code);

    [LoggerMessage(Level = LogLevel.Warning, Message = "AI analysis {Id}: answer {Call} was not a valid report")]
    private partial void LogInvalidAnswer(Guid id, int call);

    [LoggerMessage(Level = LogLevel.Warning, Message = "AI provider call failed ({Kind}) for analysis {Id}")]
    private partial void LogProviderFailure(AiProviderErrorKind kind, Guid id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "AI analysis {Id} hit a transient failure")]
    private partial void LogTransientFailure(Exception exception, Guid id);

    [LoggerMessage(Level = LogLevel.Error, Message = "AI analysis {Id} failed unexpectedly")]
    private partial void LogUnexpectedFailure(Exception exception, Guid id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recovered {Count} AI analyses abandoned while running")]
    private partial void LogRecovered(int count);
}
