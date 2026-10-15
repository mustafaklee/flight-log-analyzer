using System.Globalization;
using FlightLog.Application.Abstractions;
using FlightLog.Application.Processing;
using FlightLog.Domain.Analysis;
using FlightLog.Domain.FlightData;
using FlightLog.Domain.Flights;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlightLog.Application.Analysis;

/// <summary>An analysis run this worker has moved to Running.</summary>
public sealed record ClaimedAnalysis(Guid RunId, Guid FlightId, Guid LogFileId, string CorrelationId);

/// <summary>Executes queued analysis runs (worker side).</summary>
public interface IAnalysisService
{
    /// <summary>Claims the oldest queued run that is due (database transport); null when none is queued.</summary>
    Task<ClaimedAnalysis?> ClaimNextAsync(string workerId, CancellationToken cancellationToken);

    /// <summary>
    /// Claims the given run. Null when there is nothing to do: the run is not queued anymore, a newer run replaced
    /// it, or its file left AnalysisPending (cancelled or reprocessed); such a run is closed as failed.
    /// </summary>
    Task<ClaimedAnalysis?> ClaimAsync(Guid runId, string workerId, CancellationToken cancellationToken);

    /// <summary>Runs the rules and stores findings, run and file status in one transaction.</summary>
    Task<WorkOutcome> RunAsync(ClaimedAnalysis claim, CancellationToken shutdown);

    /// <summary>Retries (or fails) runs left running by a worker that died.</summary>
    Task<int> RecoverStaleAsync(CancellationToken cancellationToken);
}

internal sealed partial class AnalysisService(
    IApplicationDbContext db,
    IFileStorage storage,
    RuleEngine engine,
    TimeProvider clock,
    ITransientErrorClassifier errors,
    ProcessingMetrics metrics,
    IOptions<ProcessingOptions> options,
    ILogger<AnalysisService> logger) : IAnalysisService
{
    public async Task<ClaimedAnalysis?> ClaimNextAsync(string workerId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var candidate = await db.AnalysisRuns.AsNoTracking()
            .Where(r => r.Status == AnalysisRunStatus.Queued && (r.NextAttemptAt == null || r.NextAttemptAt <= now))
            .OrderBy(r => r.CreatedAt).ThenBy(r => r.Id)
            .Select(r => (Guid?)r.Id)
            .FirstOrDefaultAsync(cancellationToken);
        return candidate is { } id ? await ClaimAsync(id, workerId, cancellationToken) : null;
    }

    public async Task<ClaimedAnalysis?> ClaimAsync(Guid runId, string workerId, CancellationToken cancellationToken)
    {
        var run = await db.AnalysisRuns.SingleOrDefaultAsync(r => r.Id == runId, cancellationToken);
        if (run is null || run.Status != AnalysisRunStatus.Queued)
        {
            return null;
        }

        var now = clock.GetUtcNow();
        var file = await db.FlightLogFiles.SingleOrDefaultAsync(f => f.Id == run.LogFileId, cancellationToken);
        var newer = await db.AnalysisRuns.AnyAsync(r => r.LogFileId == run.LogFileId && r.Id != run.Id && r.CreatedAt > run.CreatedAt, cancellationToken);
        try
        {
            if (file is null || file.Status != ProcessingStatus.AnalysisPending || newer)
            {
                run.Fail("analysis.superseded", "The log file was cancelled or reprocessed before this analysis started.", now);
                await db.SaveChangesAsync(cancellationToken);
                return null;
            }

            run.Start(workerId, RuleEngine.Version, now);
            file.TransitionTo(ProcessingStatus.Analyzing, now);

            // Row versions of run and file make this a compare-and-set.
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return null;
        }

        return new ClaimedAnalysis(run.Id, run.FlightId, run.LogFileId, run.CorrelationId);
    }

    public async Task<WorkOutcome> RunAsync(ClaimedAnalysis claim, CancellationToken shutdown)
    {
        ArgumentNullException.ThrowIfNull(claim);
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = claim.CorrelationId, ["AnalysisRunId"] = claim.RunId });
        var started = clock.GetTimestamp();
        WorkOutcome outcome;
        try
        {
            outcome = await ExecuteAsync(claim, shutdown);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            outcome = await EndWithoutResultAsync(claim, WorkOutcome.Requeued, null, null);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The flight was deleted or the file changed while the results were saved.
            db.ChangeTracker.Clear();
            outcome = WorkOutcome.Skipped;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && errors.IsTransient(ex))
        {
            LogTransientFailure(ex, claim.RunId);
            outcome = await EndWithoutResultAsync(
                claim, WorkOutcome.RetryScheduled, "analysis.transient_failure",
                $"The analysis was interrupted by a temporary infrastructure problem (correlation id {claim.CorrelationId}).");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogUnexpectedFailure(ex, claim.RunId);
            outcome = await EndWithoutResultAsync(
                claim, WorkOutcome.Failed, "analysis.failed", $"The analysis failed because of an internal error (correlation id {claim.CorrelationId}).");
        }

        metrics.AnalysisEnded(outcome, clock.GetElapsedTime(started));
        return outcome;
    }

    public async Task<int> RecoverStaleAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var threshold = now - options.Value.AnalysisStaleAfter;
        var stale = await db.AnalysisRuns.Where(r => r.Status == AnalysisRunStatus.Running && r.StartedAt < threshold).ToListAsync(cancellationToken);
        if (stale.Count == 0)
        {
            return 0;
        }

        var fileIds = stale.Select(r => r.LogFileId).ToList();
        var files = await db.FlightLogFiles.Where(f => fileIds.Contains(f.Id)).ToDictionaryAsync(f => f.Id, cancellationToken);
        foreach (var run in stale)
        {
            Abandon(run, files.GetValueOrDefault(run.LogFileId), "analysis.interrupted", "The analysis stopped unexpectedly (the worker stopped responding).", now);
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

    private async Task<WorkOutcome> ExecuteAsync(ClaimedAnalysis claim, CancellationToken cancellationToken)
    {
        var run = await db.AnalysisRuns.SingleAsync(r => r.Id == claim.RunId, cancellationToken);
        var file = await db.FlightLogFiles.SingleAsync(f => f.Id == claim.LogFileId, cancellationToken);
        var flight = await db.Flights.AsNoTracking().SingleAsync(f => f.Id == claim.FlightId, cancellationToken);
        var events = await db.FlightEvents.AsNoTracking().Where(e => e.FlightId == flight.Id).ToListAsync(cancellationToken);
        var parameters = await db.FlightParameters.AsNoTracking().Where(p => p.FlightId == flight.Id).ToListAsync(cancellationToken);
        var span = await db.FlightSignalSeries.AsNoTracking().Where(s => s.FlightId == flight.Id && s.FirstTimestampUs != null)
            .GroupBy(s => 1).Select(g => new { First = g.Min(s => s.FirstTimestampUs), Last = g.Max(s => s.LastTimestampUs) })
            .SingleOrDefaultAsync(cancellationToken);
        var processingJob = await db.LogProcessingJobs.AsNoTracking()
            .Where(j => j.LogFileId == file.Id && j.Outcome == ProcessingJobOutcome.Succeeded)
            .OrderByDescending(j => j.FinishedAt)
            .Select(j => new { j.Id, j.NormalizationVersion, j.ParserName, j.ParserVersion })
            .FirstOrDefaultAsync(cancellationToken);

        // The log start the parser determined (PX4: header timestamp); some topics are logged with timestamp 0.
        var firstUs = flight.LogStartTimestampUs
            ?? Math.Min(span?.First ?? long.MaxValue, events.Count > 0 ? events.Min(e => e.TimestampUs) : long.MaxValue);
        var lastUs = Math.Max(span?.Last ?? long.MinValue, events.Count > 0 ? events.Max(e => e.TimestampUs) : long.MinValue);
        var log = firstUs <= lastUs ? new TimeRange(firstUs, lastUs) : new TimeRange(0, 0);

        var signals = await FlightSignalSource.CreateAsync(db, storage, flight.Id, cancellationToken);
        var context = new AnalysisContext(flight.Autopilot, flight.VehicleType, log, events, parameters, signals);
        var result = await engine.RunAsync(context, run.Id, flight.Id, cancellationToken);

        var now = clock.GetUtcNow();
        var input = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["logFromUs"] = log.StartUs.ToString(CultureInfo.InvariantCulture),
            ["logToUs"] = log.EndUs.ToString(CultureInfo.InvariantCulture),
            ["armedPeriods"] = context.ArmedIntervals.Count.ToString(CultureInfo.InvariantCulture),
            ["analyzedSeconds"] = context.Windows.Sum(w => w.DurationSeconds).ToString("0.###", CultureInfo.InvariantCulture),
            ["signals"] = signals.SignalNames.Count.ToString(CultureInfo.InvariantCulture),
            ["events"] = events.Count.ToString(CultureInfo.InvariantCulture),
            ["parameters"] = parameters.Count.ToString(CultureInfo.InvariantCulture),
        };
        if (processingJob is not null)
        {
            input["processingJobId"] = processingJob.Id.ToString();
            input["parser"] = $"{processingJob.ParserName} {processingJob.ParserVersion}";
            input["normalizationVersion"] = processingJob.NormalizationVersion ?? "";
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.AnalysisFindings.AddRange(result.Findings);
        db.AnalysisRuleResults.AddRange(result.RuleResults);
        run.Succeed(result.Counts, input, now);
        file.TransitionTo(ProcessingStatus.Completed, now);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        foreach (var finding in result.Findings)
        {
            metrics.Finding(finding.RuleId, finding.Severity.ToString());
        }

        LogSucceeded(run.Id, result.Counts.Total, result.Counts.RulesApplied, result.Counts.RulesNotApplicable, result.Counts.RulesFailed);
        return WorkOutcome.Succeeded;
    }

    /// <summary>Ends a run without findings: back to the queue (shutdown, retry) or failed.</summary>
    private async Task<WorkOutcome> EndWithoutResultAsync(ClaimedAnalysis claim, WorkOutcome wanted, string? code, string? message)
    {
        var token = CancellationToken.None;
        db.ChangeTracker.Clear();
        var run = await db.AnalysisRuns.SingleOrDefaultAsync(r => r.Id == claim.RunId, token);
        var file = await db.FlightLogFiles.SingleOrDefaultAsync(f => f.Id == claim.LogFileId, token);
        if (run is null || run.Status != AnalysisRunStatus.Running)
        {
            return WorkOutcome.Skipped;
        }

        var now = clock.GetUtcNow();
        var analyzing = file?.Status == ProcessingStatus.Analyzing;
        var outcome = wanted;
        switch (wanted)
        {
            case WorkOutcome.Requeued:
                run.Requeue(null, countsAsRetry: false);
                if (analyzing)
                {
                    file!.TransitionTo(ProcessingStatus.AnalysisPending, now);
                }

                break;
            case WorkOutcome.RetryScheduled:
                outcome = Abandon(run, file, code!, message!, now);
                break;
            default:
                run.Fail(code!, message!, now);
                if (analyzing)
                {
                    file!.Fail(code!, message!, now);
                }

                break;
        }

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
    }

    /// <summary>Retries a run that failed for a transient reason, or fails it when the retries are used up.</summary>
    private WorkOutcome Abandon(AnalysisRun run, FlightLogFile? file, string code, string message, DateTimeOffset now)
    {
        var analyzing = file?.Status == ProcessingStatus.Analyzing;
        if (run.RetryCount < options.Value.MaxAutomaticRetries && analyzing)
        {
            run.Requeue(now + options.Value.RetryDelay(run.RetryCount + 1), countsAsRetry: true);
            file!.TransitionTo(ProcessingStatus.AnalysisPending, now);
            return WorkOutcome.RetryScheduled;
        }

        const string Exhausted = "analysis.retries_exhausted";
        var text = $"{message} Gave up after {run.RetryCount} automatic retries.";
        run.Fail(Exhausted, text, now);
        if (analyzing)
        {
            file!.Fail(Exhausted, text, now);
        }

        return WorkOutcome.RetriesExhausted;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Analysis run {RunId} finished: {Findings} findings, {Applied} rules applied, {NotApplicable} not applicable, {Failed} failed")]
    private partial void LogSucceeded(Guid runId, int findings, int applied, int notApplicable, int failed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Analysis run {RunId} hit a transient failure")]
    private partial void LogTransientFailure(Exception exception, Guid runId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Analysis run {RunId} failed unexpectedly")]
    private partial void LogUnexpectedFailure(Exception exception, Guid runId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recovered {Count} analysis runs abandoned while running")]
    private partial void LogRecovered(int count);
}
