using FlightLog.Application.Abstractions;
using FlightLog.Application.Authorization;
using FlightLog.Application.Common;
using FlightLog.Domain.Analysis;
using FlightLog.Domain.Teams;
using Microsoft.EntityFrameworkCore;

namespace FlightLog.Application.Collaboration;

internal static class FlightAccess
{
    /// <summary>The flight's team and the caller's role in it; a flight of another team is "not found".</summary>
    public static async Task<(Guid TeamId, TeamRole Role)> EnsureAsync(
        IApplicationDbContext db, ITeamAccessService access, Guid flightId, TeamRole required, CancellationToken cancellationToken)
    {
        var teamId = await db.Flights.Where(f => f.Id == flightId).Select(f => (Guid?)f.TeamId).SingleOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException("Flight", flightId);
        var role = await access.EnsureResourceRoleAsync(teamId, required, "Flight", flightId, cancellationToken);
        return (teamId, role);
    }

    public static async Task<AnalysisFinding> FindingOfFlightAsync(IApplicationDbContext db, Guid flightId, Guid findingId, CancellationToken cancellationToken) =>
        await db.AnalysisFindings.AsNoTracking().SingleOrDefaultAsync(f => f.Id == findingId && f.FlightId == flightId, cancellationToken)
            ?? throw new NotFoundException("Finding", findingId);
}
