using FlightLog.Application.Abstractions;
using FlightLog.Application.Common;
using FlightLog.Domain.Teams;
using Microsoft.EntityFrameworkCore;

namespace FlightLog.Application.Authorization;

internal sealed class TeamAccessService(IApplicationDbContext db, ICurrentUser currentUser) : ITeamAccessService
{
    public async Task<TeamRole?> GetRoleAsync(Guid teamId, CancellationToken cancellationToken)
    {
        var role = await db.TeamMembers
            .Where(m => m.TeamId == teamId && m.UserId == currentUser.UserId)
            .Select(m => (TeamRole?)m.Role)
            .FirstOrDefaultAsync(cancellationToken);

        if (role is not null || !currentUser.IsAdministrator)
        {
            return role;
        }

        // Administrators manage every existing team as if they were its project manager.
        var exists = await db.Teams.AnyAsync(t => t.Id == teamId, cancellationToken);
        return exists ? TeamRole.ProjectManager : null;
    }

    public async Task<TeamRole> EnsureRoleAsync(Guid teamId, TeamRole required, CancellationToken cancellationToken)
    {
        var role = await GetRoleAsync(teamId, cancellationToken)
            ?? throw new NotFoundException("Team", teamId);

        if (!role.Includes(required))
        {
            throw new ForbiddenException($"This operation requires the {required} role in the team.");
        }

        return role;
    }

    public async Task<TeamRole> EnsureResourceRoleAsync(Guid teamId, TeamRole required, string resource, Guid resourceId, CancellationToken cancellationToken)
    {
        var role = await GetRoleAsync(teamId, cancellationToken)
            ?? throw new NotFoundException(resource, resourceId);

        if (!role.Includes(required))
        {
            throw new ForbiddenException($"This operation requires the {required} role in the team.");
        }

        return role;
    }

    public async Task<IReadOnlyCollection<Guid>?> GetReadableTeamIdsAsync(CancellationToken cancellationToken)
    {
        if (currentUser.IsAdministrator)
        {
            return null;
        }

        return await db.TeamMembers
            .Where(m => m.UserId == currentUser.UserId)
            .Select(m => m.TeamId)
            .ToListAsync(cancellationToken);
    }
}
