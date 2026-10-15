using FlightLog.Application.Abstractions;
using FlightLog.Application.Authorization;
using FlightLog.Application.Flights;
using FlightLog.Domain.Analysis;
using FlightLog.Domain.Flights;
using FlightLog.Domain.Teams;
using Microsoft.EntityFrameworkCore;

namespace FlightLog.Application.Dashboard;

/// <summary>A notable finding of the latest analysis of a flight.</summary>
public sealed record DashboardFindingDto(
    Guid FindingId,
    Guid RunId,
    Guid FlightId,
    string FlightName,
    string RuleId,
    string Title,
    FindingSeverity Severity,
    long StartUs,
    DateTimeOffset FlightOccurredAt);

/// <summary>Figures of one team for the dashboard. Findings count the latest successful analysis of each flight.</summary>
public sealed record DashboardDto(
    Guid TeamId,
    int ProjectCount,
    int AircraftCount,
    int FlightCount,
    int FlightsLast30Days,
    double TotalFlightHours,
    IReadOnlyDictionary<ProcessingStatus, int> FilesByStatus,
    IReadOnlyDictionary<FindingSeverity, int> FindingsBySeverity,
    int FlightsWithCriticalFindings,
    IReadOnlyList<FlightListItemDto> RecentFlights,
    IReadOnlyList<DashboardFindingDto> RecentFindings);

public interface IDashboardService
{
    Task<DashboardDto> GetAsync(Guid teamId, CancellationToken cancellationToken);
}

internal sealed class DashboardService(IApplicationDbContext db, ITeamAccessService access, IFlightService flights, TimeProvider clock) : IDashboardService
{
    private const int RecentCount = 8;

    public async Task<DashboardDto> GetAsync(Guid teamId, CancellationToken cancellationToken)
    {
        await access.EnsureRoleAsync(teamId, TeamRole.Viewer, cancellationToken);
        var since = clock.GetUtcNow().AddDays(-30);

        var teamFlights = db.Flights.AsNoTracking().Where(f => f.TeamId == teamId);
        var projectCount = await db.Projects.CountAsync(p => p.TeamId == teamId, cancellationToken);
        var aircraftCount = await db.Aircraft.CountAsync(a => a.TeamId == teamId, cancellationToken);
        var flightCount = await teamFlights.CountAsync(cancellationToken);
        var recentCount = await teamFlights.CountAsync(f => f.OccurredAt >= since, cancellationToken);
        var totalUs = await teamFlights.SumAsync(f => f.DurationUs ?? 0, cancellationToken);
        var files = await db.FlightLogFiles.AsNoTracking().Where(f => f.TeamId == teamId)
            .GroupBy(f => f.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(cancellationToken);

        // The latest successful run of each flight; older runs are superseded.
        var latestRuns = db.AnalysisRuns.AsNoTracking().Where(r => r.TeamId == teamId && r.Status == AnalysisRunStatus.Succeeded
            && !db.AnalysisRuns.Any(o => o.FlightId == r.FlightId && o.Status == AnalysisRunStatus.Succeeded && o.CreatedAt > r.CreatedAt));
        var totals = await latestRuns.GroupBy(_ => 1).Select(g => new
        {
            Info = g.Sum(r => r.InfoCount),
            Warning = g.Sum(r => r.WarningCount),
            Critical = g.Sum(r => r.CriticalCount),
            FlightsWithCritical = g.Count(r => r.CriticalCount > 0),
        }).FirstOrDefaultAsync(cancellationToken);

        var findings = await db.AnalysisFindings.AsNoTracking()
            .Where(f => f.Severity >= FindingSeverity.Warning && latestRuns.Any(r => r.Id == f.RunId))
            .Join(db.Flights, f => f.FlightId, fl => fl.Id, (f, fl) => new { Finding = f, Flight = fl })
            .OrderByDescending(x => x.Flight.OccurredAt).ThenByDescending(x => x.Finding.Severity).ThenBy(x => x.Finding.StartUs)
            .Take(RecentCount)
            .Select(x => new DashboardFindingDto(
                x.Finding.Id, x.Finding.RunId, x.Flight.Id, x.Flight.Name, x.Finding.RuleId, x.Finding.Title, x.Finding.Severity, x.Finding.StartUs, x.Flight.OccurredAt))
            .ToListAsync(cancellationToken);

        var recent = await flights.ListAsync(new FlightQuery(TeamId: teamId, PageSize: RecentCount), cancellationToken);

        return new DashboardDto(
            teamId,
            projectCount,
            aircraftCount,
            flightCount,
            recentCount,
            Math.Round(totalUs / 3_600_000_000d, 2),
            files.ToDictionary(f => f.Key, f => f.Count),
            new Dictionary<FindingSeverity, int>
            {
                [FindingSeverity.Info] = totals?.Info ?? 0,
                [FindingSeverity.Warning] = totals?.Warning ?? 0,
                [FindingSeverity.Critical] = totals?.Critical ?? 0,
            },
            totals?.FlightsWithCritical ?? 0,
            recent.Items,
            findings);
    }
}
