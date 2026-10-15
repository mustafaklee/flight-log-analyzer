using FlightLog.Application.Abstractions;
using FlightLog.Contracts;
using FlightLog.Domain.Analysis;
using FlightLog.Domain.Flights;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlightLog.Application.Messaging;

/// <summary>Housekeeping of the outbox; does nothing with the database transport.</summary>
public interface IOutboxMaintenance
{
    Task RunAsync(CancellationToken cancellationToken);
}

internal sealed partial class OutboxMaintenance(
    IApplicationDbContext db, TimeProvider clock, IOptions<MessagingOptions> options, ILogger<OutboxMaintenance> logger) : IOutboxMaintenance
{
    private const int BatchSize = 100;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (options.Value.Transport != MessageTransport.RabbitMq)
        {
            return;
        }

        var now = clock.GetUtcNow();
        await db.OutboxMessages.Where(m => m.PublishedAt != null && m.PublishedAt < now - options.Value.OutboxRetention).ExecuteDeleteAsync(cancellationToken);
        await ReannounceOrphansAsync(now, cancellationToken);
    }

    /// <summary>
    /// Work can stay queued without a message when a message was dead-lettered after an infrastructure failure of
    /// the consumer, or lost by the broker. PostgreSQL is the source of truth, so such work is announced again.
    /// </summary>
    private async Task ReannounceOrphansAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var cutoff = now - options.Value.OrphanAfter;
        var files = await db.FlightLogFiles.AsNoTracking()
            .Where(f => f.Status == ProcessingStatus.Queued && f.StatusChangedAt < cutoff && (f.NextAttemptAt == null || f.NextAttemptAt < cutoff)
                && !db.OutboxMessages.Any(m => m.AggregateId == f.Id && (m.CreatedAt >= cutoff || m.PublishedAt == null)))
            .OrderBy(f => f.StatusChangedAt)
            .Select(f => new { f.Id, f.TeamId })
            .Take(BatchSize)
            .ToListAsync(cancellationToken);
        var runs = await db.AnalysisRuns.AsNoTracking()
            .Where(r => r.Status == AnalysisRunStatus.Queued && r.CreatedAt < cutoff && (r.NextAttemptAt == null || r.NextAttemptAt < cutoff)
                && !db.OutboxMessages.Any(m => m.AggregateId == r.Id && (m.CreatedAt >= cutoff || m.PublishedAt == null)))
            .OrderBy(r => r.CreatedAt)
            .Select(r => new { r.Id, r.FlightId, r.TeamId, r.CorrelationId })
            .Take(BatchSize)
            .ToListAsync(cancellationToken);
        if (files.Count == 0 && runs.Count == 0)
        {
            return;
        }

        foreach (var f in files)
        {
            db.OutboxMessages.Add(OutboxMessages.Create(new ProcessLogFileV1(f.Id, f.TeamId, Guid.NewGuid().ToString("N")), f.Id, now, null));
        }

        foreach (var r in runs)
        {
            db.OutboxMessages.Add(OutboxMessages.Create(new AnalyzeFlightV1(r.Id, r.FlightId, r.TeamId, r.CorrelationId), r.Id, now, null));
        }

        await db.SaveChangesAsync(cancellationToken);
        LogReannounced(files.Count, runs.Count);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Announced {Files} queued files and {Runs} queued analysis runs again (no message for them)")]
    private partial void LogReannounced(int files, int runs);
}
