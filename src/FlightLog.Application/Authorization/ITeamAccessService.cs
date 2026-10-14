using FlightLog.Domain.Teams;

namespace FlightLog.Application.Authorization;

/// <summary>
/// Resolves what the current user may do inside a team. All resource-level authorization of
/// team-owned data goes through this service.
/// </summary>
public interface ITeamAccessService
{
    /// <summary>The effective role of the current user in the team, or null when the user has no access.</summary>
    Task<TeamRole?> GetRoleAsync(Guid teamId, CancellationToken cancellationToken);

    /// <summary>
    /// Ensures the current user has at least <paramref name="required"/> in the team.
    /// Throws <see cref="Common.NotFoundException"/> for non-members and <see cref="Common.ForbiddenException"/>
    /// for members with an insufficient role.
    /// </summary>
    Task<TeamRole> EnsureRoleAsync(Guid teamId, TeamRole required, CancellationToken cancellationToken);

    /// <summary>
    /// Like <see cref="EnsureRoleAsync"/> for a resource owned by the team, but reports a non-member as
    /// "resource not found" so that resource ids cannot be probed across teams.
    /// </summary>
    Task<TeamRole> EnsureResourceRoleAsync(Guid teamId, TeamRole required, string resource, Guid resourceId, CancellationToken cancellationToken);

    /// <summary>Ids of all teams the current user can read. Administrators get null, meaning "all teams".</summary>
    Task<IReadOnlyCollection<Guid>?> GetReadableTeamIdsAsync(CancellationToken cancellationToken);
}
