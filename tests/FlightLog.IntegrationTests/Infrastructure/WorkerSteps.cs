using FlightLog.Application.Processing;
using FlightLog.Domain.Flights;
using FlightLog.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FlightLog.IntegrationTests.Infrastructure;

/// <summary>Runs the worker's steps in the test process (database transport), so that tests control the timing.</summary>
internal static class WorkerSteps
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Runs the worker loop until nothing is due, optionally leaving one file queued.</summary>
    public static async Task ProcessQueueAsync(this ApiFactory factory, Guid? except = null)
    {
        if (except is { } id)
        {
            await factory.SetFileStatusAsync(id, ProcessingStatus.Cancelled);
        }

        var runner = factory.Services.GetRequiredService<WorkRunner>();
        for (var i = 0; i < 100 && await runner.RunOnceAsync("test-worker", Ct); i++)
        {
        }

        if (except is { } restore)
        {
            await factory.SetFileStatusAsync(restore, ProcessingStatus.Queued);
        }
    }

    public static async Task SetFileStatusAsync(this ApiFactory factory, Guid fileId, ProcessingStatus status)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FlightLogDbContext>();
        await db.FlightLogFiles.Where(f => f.Id == fileId).ExecuteUpdateAsync(s => s.SetProperty(f => f.Status, status), Ct);
    }
}
