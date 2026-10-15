using FlightLog.Application.Abstractions;
using FlightLog.Application.Analysis;
using FlightLog.Application.Messaging;
using FlightLog.Domain.Flights;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FlightLog.Application.Processing;

/// <summary>
/// Executes queued work: parsing a log file or running an analysis. Both transports use it, the database poller
/// (<see cref="RunOnceAsync"/>) and the message consumer (<see cref="ProcessFileAsync"/>, <see cref="AnalyzeAsync"/>).
/// Each step uses its own DI scope (and so its own DbContext); while a file is processed a second scope watches
/// its status so that a cancellation by the user stops the work.
/// </summary>
public sealed class WorkRunner(IServiceScopeFactory scopes, IOptions<ProcessingOptions> options, TimeProvider clock)
{
    private DateTimeOffset _nextMaintenance = DateTimeOffset.MinValue;

    /// <summary>Database transport: runs one queued item. Returns true when something was claimed (more may be waiting).</summary>
    public async Task<bool> RunOnceAsync(string workerId, CancellationToken stoppingToken)
    {
        await RunMaintenanceIfDueAsync(stoppingToken);

        await using (var scope = scopes.CreateAsyncScope())
        {
            var claim = await scope.ServiceProvider.GetRequiredService<ILogProcessingService>().ClaimNextAsync(workerId, stoppingToken);
            if (claim is not null)
            {
                if (claim.Job is { } job)
                {
                    await ProcessClaimedAsync(scope.ServiceProvider, job, stoppingToken);
                }

                return true;
            }
        }

        await using (var scope = scopes.CreateAsyncScope())
        {
            var analysis = scope.ServiceProvider.GetRequiredService<IAnalysisService>();
            var claim = await analysis.ClaimNextAsync(workerId, stoppingToken);
            if (claim is null)
            {
                // A superseded run was closed, or nothing is queued; ask again only if runs remain queued.
                var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
                var now = clock.GetUtcNow();
                return await db.AnalysisRuns.AnyAsync(
                    r => r.Status == Domain.Analysis.AnalysisRunStatus.Queued && (r.NextAttemptAt == null || r.NextAttemptAt <= now), stoppingToken);
            }

            await analysis.RunAsync(claim, stoppingToken);
            return true;
        }
    }

    /// <summary>Processes the file a message announced. <see cref="WorkOutcome.Skipped"/> when it is not queued (anymore).</summary>
    public async Task<WorkOutcome> ProcessFileAsync(Guid logFileId, string workerId, CancellationToken stoppingToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var claim = await scope.ServiceProvider.GetRequiredService<ILogProcessingService>().ClaimAsync(logFileId, workerId, stoppingToken);
        return claim.Job is { } job ? await ProcessClaimedAsync(scope.ServiceProvider, job, stoppingToken) : WorkOutcome.Skipped;
    }

    /// <summary>Runs the analysis a message announced. <see cref="WorkOutcome.Skipped"/> when it is not queued (anymore).</summary>
    public async Task<WorkOutcome> AnalyzeAsync(Guid runId, string workerId, CancellationToken stoppingToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var analysis = scope.ServiceProvider.GetRequiredService<IAnalysisService>();
        var claim = await analysis.ClaimAsync(runId, workerId, stoppingToken);
        return claim is null ? WorkOutcome.Skipped : await analysis.RunAsync(claim, stoppingToken);
    }

    /// <summary>Recovers work abandoned by crashed workers and re-announces lost messages; at most once a minute.</summary>
    public async Task RunMaintenanceIfDueAsync(CancellationToken stoppingToken)
    {
        if (clock.GetUtcNow() < _nextMaintenance)
        {
            return;
        }

        await using (var scope = scopes.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ILogProcessingService>().RecoverStaleAsync(stoppingToken);
        }

        await using (var scope = scopes.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IAnalysisService>().RecoverStaleAsync(stoppingToken);
        }

        await using (var scope = scopes.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IOutboxMaintenance>().RunAsync(stoppingToken);
        }

        _nextMaintenance = clock.GetUtcNow() + TimeSpan.FromMinutes(1);
    }

    private async Task<WorkOutcome> ProcessClaimedAsync(IServiceProvider services, ClaimedJob job, CancellationToken stoppingToken)
    {
        using var jobCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        using var watchStop = new CancellationTokenSource();
        var watcher = WatchForCancellationAsync(job.LogFileId, jobCancellation, watchStop.Token);
        try
        {
            return await services.GetRequiredService<ILogProcessingService>().ProcessAsync(job, jobCancellation.Token, stoppingToken);
        }
        finally
        {
            await watchStop.CancelAsync();
            await watcher;
        }
    }

    private async Task WatchForCancellationAsync(Guid logFileId, CancellationTokenSource jobCancellation, CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(options.Value.CancellationCheckInterval, clock, stop);
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
                var status = await db.FlightLogFiles.AsNoTracking().Where(f => f.Id == logFileId).Select(f => (ProcessingStatus?)f.Status).SingleOrDefaultAsync(stop);
                if (status != ProcessingStatus.Processing)
                {
                    await jobCancellation.CancelAsync();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
    }
}
