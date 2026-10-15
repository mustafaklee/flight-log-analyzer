using FlightLog.Application.Abstractions;
using FlightLog.Application.Common;
using FlightLog.Domain.Analysis;
using FlightLog.Domain.FlightData;
using FlightLog.Domain.Flights;
using Microsoft.EntityFrameworkCore;

namespace FlightLog.Application.Processing;

public sealed record DurationStats(int Count, double? AverageMs, double? P95Ms, double? MaxMs);

public sealed record CodeCount(string Code, int Count);

public sealed record ProcessingStatsDto(
    DateTimeOffset From,
    DateTimeOffset To,
    IReadOnlyDictionary<string, int> FilesByStatus,
    IReadOnlyDictionary<string, int> JobsByOutcome,
    double? JobFailureRate,
    IReadOnlyList<CodeCount> FailureCodes,
    int AutomaticRetries,
    DurationStats JobDuration,
    DurationStats QueueWait,
    long? MaxAllocatedBytes,
    IReadOnlyDictionary<string, int> AnalysisRunsByStatus,
    DurationStats AnalysisDuration,
    int OutboxPending,
    DateTimeOffset? OldestPendingMessageAt,
    IReadOnlyList<QueueDepth> Queues);

/// <summary>Processing figures from the database (and broker), for administrators.</summary>
public interface IProcessingStatsService
{
    Task<ProcessingStatsDto> GetAsync(TimeSpan window, CancellationToken cancellationToken);
}

internal sealed class ProcessingStatsService(IApplicationDbContext db, ICurrentUser currentUser, IMessageQueueMonitor queues, TimeProvider clock) : IProcessingStatsService
{
    /// <summary>Durations beyond this many jobs in the window are not loaded; the counts stay exact.</summary>
    private const int MaxSamples = 20000;

    private static readonly HashSet<string> TransientCodes = new(StringComparer.Ordinal) { "processing.transient_failure", "processing.interrupted" };

    public async Task<ProcessingStatsDto> GetAsync(TimeSpan window, CancellationToken cancellationToken)
    {
        if (!currentUser.IsAdministrator)
        {
            throw new ForbiddenException("Only administrators can see processing statistics.");
        }

        var to = clock.GetUtcNow();
        var from = to - window;

        var files = await db.FlightLogFiles.AsNoTracking().GroupBy(f => f.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(cancellationToken);
        var jobs = db.LogProcessingJobs.AsNoTracking().Where(j => j.StartedAt >= from);
        var outcomes = await jobs.GroupBy(j => j.Outcome).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(cancellationToken);
        var codeRows = await jobs.Where(j => j.FailureCode != null).GroupBy(j => j.FailureCode!).Select(g => new { Code = g.Key, Count = g.Count() })
            .OrderByDescending(c => c.Count).Take(20).ToListAsync(cancellationToken);
        var codes = codeRows.Select(c => new CodeCount(c.Code, c.Count)).ToList();
        var samples = await jobs.OrderByDescending(j => j.StartedAt).Take(MaxSamples)
            .Select(j => new { j.DurationMs, j.QueueWaitMs, j.AllocatedBytes, j.Outcome }).ToListAsync(cancellationToken);

        var runs = db.AnalysisRuns.AsNoTracking().Where(r => r.CreatedAt >= from);
        var runStatus = await runs.GroupBy(r => r.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(cancellationToken);
        var runDurations = await runs.Where(r => r.DurationMs != null).OrderByDescending(r => r.CreatedAt).Take(MaxSamples)
            .Select(r => r.DurationMs!.Value).ToListAsync(cancellationToken);

        var pending = db.OutboxMessages.AsNoTracking().Where(m => m.PublishedAt == null);
        var outboxPending = await pending.CountAsync(cancellationToken);
        var oldestPending = await pending.OrderBy(m => m.CreatedAt).Select(m => (DateTimeOffset?)m.CreatedAt).FirstOrDefaultAsync(cancellationToken);

        var finished = outcomes.Where(o => o.Key is ProcessingJobOutcome.Succeeded or ProcessingJobOutcome.Failed).Sum(o => o.Count);
        var failed = outcomes.Where(o => o.Key == ProcessingJobOutcome.Failed).Sum(o => o.Count);
        return new ProcessingStatsDto(
            from,
            to,
            files.ToDictionary(f => f.Key.ToString(), f => f.Count),
            outcomes.ToDictionary(o => o.Key.ToString(), o => o.Count),
            finished == 0 ? null : Math.Round((double)failed / finished, 4),
            codes,
            codes.Where(c => TransientCodes.Contains(c.Code)).Sum(c => c.Count),
            Durations(samples.Where(s => s.Outcome != ProcessingJobOutcome.Running && s.DurationMs is not null).Select(s => (double)s.DurationMs!.Value)),
            Durations(samples.Select(s => (double)s.QueueWaitMs)),
            samples.Max(s => (long?)s.AllocatedBytes),
            runStatus.ToDictionary(r => r.Key.ToString(), r => r.Count),
            Durations(runDurations.Select(d => (double)d)),
            outboxPending,
            oldestPending,
            await queues.GetDepthsAsync(cancellationToken));
    }

    private static DurationStats Durations(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0)
        {
            return new DurationStats(0, null, null, null);
        }

        var p95 = sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * 0.95) - 1, 0, sorted.Length - 1)];
        return new DurationStats(sorted.Length, Math.Round(sorted.Average(), 1), p95, sorted[^1]);
    }
}
