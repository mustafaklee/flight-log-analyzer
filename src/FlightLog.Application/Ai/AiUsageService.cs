using FlightLog.Application.Abstractions;
using FlightLog.Domain.Analysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FlightLog.Application.Ai;

public sealed record AiUsageTotalsDto(
    int Analyses,
    int Succeeded,
    int Failed,
    int Pending,
    int ProviderCalls,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    decimal CostUsd,
    double? AverageLatencyMs);

public sealed record AiUsageByDayDto(DateOnly Day, int Analyses, int Failed, long InputTokens, long OutputTokens, decimal CostUsd);

public sealed record AiUsageByModelDto(string Provider, string Model, int Analyses, long InputTokens, long OutputTokens, decimal CostUsd, int Unpriced);

public sealed record AiUsageByTeamDto(Guid TeamId, string TeamName, int Analyses, decimal CostUsd);

public sealed record AiFailureCountDto(string Code, int Count);

/// <summary>AI usage over a period for administrators: volume, tokens, estimated cost, latency, failures.</summary>
public sealed record AiUsageDto(
    DateTimeOffset From,
    DateTimeOffset To,
    bool Enabled,
    string Provider,
    string Model,
    int DailyLimitPerTeam,
    AiUsageTotalsDto Totals,
    IReadOnlyList<AiUsageByDayDto> ByDay,
    IReadOnlyList<AiUsageByModelDto> ByModel,
    IReadOnlyList<AiUsageByTeamDto> ByTeam,
    IReadOnlyList<AiFailureCountDto> Failures);

public interface IAiUsageService
{
    Task<AiUsageDto> GetAsync(TimeSpan period, CancellationToken cancellationToken);
}

internal sealed class AiUsageService(IApplicationDbContext db, TimeProvider clock, IOptions<AiOptions> options) : IAiUsageService
{
    public async Task<AiUsageDto> GetAsync(TimeSpan period, CancellationToken cancellationToken)
    {
        var to = clock.GetUtcNow();
        var from = to - period;
        var rows = await db.AiAnalyses.AsNoTracking().Where(a => a.CreatedAt >= from)
            .Select(a => new
            {
                a.TeamId,
                a.Provider,
                a.Model,
                a.Status,
                a.CreatedAt,
                a.ProviderCalls,
                a.LatencyMs,
                a.InputTokens,
                a.OutputTokens,
                a.CacheReadTokens,
                a.CostUsd,
                a.FailureCode,
            })
            .ToListAsync(cancellationToken);
        var teamIds = rows.Select(r => r.TeamId).Distinct().ToList();
        var teams = await db.Teams.AsNoTracking().Where(t => teamIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Name, cancellationToken);

        var calls = rows.Sum(r => r.ProviderCalls);
        var totals = new AiUsageTotalsDto(
            rows.Count,
            rows.Count(r => r.Status == AiAnalysisStatus.Succeeded),
            rows.Count(r => r.Status == AiAnalysisStatus.Failed),
            rows.Count(r => r.Status is AiAnalysisStatus.Queued or AiAnalysisStatus.Running),
            calls,
            rows.Sum(r => r.InputTokens),
            rows.Sum(r => r.OutputTokens),
            rows.Sum(r => r.CacheReadTokens),
            rows.Sum(r => r.CostUsd ?? 0m),
            calls > 0 ? rows.Sum(r => (double)r.LatencyMs) / calls : null);

        var settings = options.Value;
        return new AiUsageDto(
            from,
            to,
            settings.Enabled,
            settings.Provider,
            settings.Model,
            settings.DailyLimitPerTeam,
            totals,
            [.. rows.GroupBy(r => DateOnly.FromDateTime(r.CreatedAt.UtcDateTime)).OrderBy(g => g.Key)
                .Select(g => new AiUsageByDayDto(g.Key, g.Count(), g.Count(r => r.Status == AiAnalysisStatus.Failed), g.Sum(r => r.InputTokens), g.Sum(r => r.OutputTokens), g.Sum(r => r.CostUsd ?? 0m)))],
            [.. rows.GroupBy(r => (r.Provider, r.Model)).OrderByDescending(g => g.Count()).ThenBy(g => g.Key.Model, StringComparer.Ordinal)
                .Select(g => new AiUsageByModelDto(g.Key.Provider, g.Key.Model, g.Count(), g.Sum(r => r.InputTokens), g.Sum(r => r.OutputTokens), g.Sum(r => r.CostUsd ?? 0m), g.Count(r => r.CostUsd is null && r.ProviderCalls > 0)))],
            [.. rows.GroupBy(r => r.TeamId).OrderByDescending(g => g.Sum(r => r.CostUsd ?? 0m)).ThenByDescending(g => g.Count())
                .Select(g => new AiUsageByTeamDto(g.Key, teams.GetValueOrDefault(g.Key, "?"), g.Count(), g.Sum(r => r.CostUsd ?? 0m)))],
            [.. rows.Where(r => r.FailureCode is not null).GroupBy(r => r.FailureCode!).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new AiFailureCountDto(g.Key, g.Count()))]);
    }
}
