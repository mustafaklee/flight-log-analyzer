using FlightLog.Application.Abstractions;
using FlightLog.Domain.Flights;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FlightLog.Application.Processing;

/// <summary>
/// Processes queued files one at a time. Each step uses its own DI scope (and so its own DbContext); while a file
/// is processed a second scope watches its status so that a cancellation by the user stops the work.
/// </summary>
public sealed class LogProcessingRunner(IServiceScopeFactory scopes, IOptions<ProcessingOptions> options, TimeProvider clock)
{
    private DateTimeOffset _nextRecovery = DateTimeOffset.MinValue;

    /// <summary>Runs one step. Returns true when a file was claimed (more work may be waiting).</summary>
    public async Task<bool> RunOnceAsync(string workerId, CancellationToken stoppingToken)
    {
        if (clock.GetUtcNow() >= _nextRecovery)
        {
            await using var recoveryScope = scopes.CreateAsyncScope();
            await recoveryScope.ServiceProvider.GetRequiredService<ILogProcessingService>().RecoverStaleAsync(stoppingToken);
            _nextRecovery = clock.GetUtcNow() + TimeSpan.FromMinutes(1);
        }

        await using var scope = scopes.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ILogProcessingService>();
        var claim = await service.ClaimNextAsync(workerId, stoppingToken);
        if (claim is null)
        {
            return false;
        }

        if (claim.Job is not { } job)
        {
            return true;
        }

        using var jobCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        using var watchStop = new CancellationTokenSource();
        var watcher = WatchForCancellationAsync(job.LogFileId, jobCancellation, watchStop.Token);
        try
        {
            await service.ProcessAsync(job, jobCancellation.Token, stoppingToken);
        }
        finally
        {
            await watchStop.CancelAsync();
            await watcher;
        }

        return true;
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
