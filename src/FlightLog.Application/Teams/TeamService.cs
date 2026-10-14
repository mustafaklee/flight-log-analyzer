using FlightLog.Application.Abstractions;
using FlightLog.Application.Authorization;
using FlightLog.Application.Common;
using FlightLog.Domain.Teams;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace FlightLog.Application.Teams;

public interface ITeamService
{
    Task<IReadOnlyList<TeamDto>> ListMineAsync(CancellationToken cancellationToken);

    Task<TeamDto> GetAsync(Guid teamId, CancellationToken cancellationToken);

    Task<TeamDto> CreateAsync(CreateTeamRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<TeamMemberDto>> ListMembersAsync(Guid teamId, CancellationToken cancellationToken);

    Task<TeamMemberDto> AddMemberAsync(Guid teamId, AddTeamMemberRequest request, CancellationToken cancellationToken);

    Task<TeamMemberDto> ChangeMemberRoleAsync(Guid teamId, Guid userId, ChangeTeamMemberRoleRequest request, CancellationToken cancellationToken);

    Task RemoveMemberAsync(Guid teamId, Guid userId, CancellationToken cancellationToken);
}

internal sealed class TeamService(
    IApplicationDbContext db,
    ICurrentUser currentUser,
    ITeamAccessService access,
    IUserDirectory users,
    TimeProvider clock,
    IValidator<CreateTeamRequest> createValidator,
    IValidator<AddTeamMemberRequest> addMemberValidator,
    IValidator<ChangeTeamMemberRoleRequest> changeRoleValidator) : ITeamService
{
    public async Task<IReadOnlyList<TeamDto>> ListMineAsync(CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        return await db.TeamMembers
            .Where(m => m.UserId == userId)
            .Join(db.Teams, m => m.TeamId, t => t.Id, (m, t) => new { Team = t, m.Role })
            .OrderBy(x => x.Team.Name)
            .Select(x => new TeamDto(x.Team.Id, x.Team.Name, x.Team.CreatedAt, x.Role, x.Team.Members.Count))
            .ToListAsync(cancellationToken);
    }

    public async Task<TeamDto> GetAsync(Guid teamId, CancellationToken cancellationToken)
    {
        var role = await access.EnsureRoleAsync(teamId, TeamRole.Viewer, cancellationToken);
        return await db.Teams
            .Where(t => t.Id == teamId)
            .Select(t => new TeamDto(t.Id, t.Name, t.CreatedAt, role, t.Members.Count))
            .SingleAsync(cancellationToken);
    }

    public async Task<TeamDto> CreateAsync(CreateTeamRequest request, CancellationToken cancellationToken)
    {
        await createValidator.EnsureValidAsync(request, cancellationToken);
        var team = Team.Create(request.Name, currentUser.UserId, clock.GetUtcNow());
        db.Teams.Add(team);
        await db.SaveChangesAsync(cancellationToken);
        return new TeamDto(team.Id, team.Name, team.CreatedAt, TeamRole.ProjectManager, team.Members.Count);
    }

    public async Task<IReadOnlyList<TeamMemberDto>> ListMembersAsync(Guid teamId, CancellationToken cancellationToken)
    {
        await access.EnsureRoleAsync(teamId, TeamRole.Viewer, cancellationToken);
        var members = await db.TeamMembers
            .Where(m => m.TeamId == teamId)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var directory = await users.GetByIdsAsync(members.Select(m => m.UserId).ToList(), cancellationToken);
        return members
            .Select(m => ToDto(m, directory.GetValueOrDefault(m.UserId)))
            .OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<TeamMemberDto> AddMemberAsync(Guid teamId, AddTeamMemberRequest request, CancellationToken cancellationToken)
    {
        await addMemberValidator.EnsureValidAsync(request, cancellationToken);
        await access.EnsureRoleAsync(teamId, TeamRole.ProjectManager, cancellationToken);

        var user = await users.FindByEmailAsync(request.Email, cancellationToken)
            ?? throw new NotFoundException("User", request.Email);

        var team = await LoadTeamAsync(teamId, cancellationToken);
        var member = team.AddMember(user.Id, request.Role, clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(member, user);
    }

    public async Task<TeamMemberDto> ChangeMemberRoleAsync(Guid teamId, Guid userId, ChangeTeamMemberRoleRequest request, CancellationToken cancellationToken)
    {
        await changeRoleValidator.EnsureValidAsync(request, cancellationToken);
        await access.EnsureRoleAsync(teamId, TeamRole.ProjectManager, cancellationToken);

        var team = await LoadTeamAsync(teamId, cancellationToken);
        if (team.RoleOf(userId) is null)
        {
            throw new NotFoundException("TeamMember", userId);
        }

        team.ChangeRole(userId, request.Role);
        await db.SaveChangesAsync(cancellationToken);

        var directory = await users.GetByIdsAsync([userId], cancellationToken);
        return ToDto(team.Members.Single(m => m.UserId == userId), directory.GetValueOrDefault(userId));
    }

    public async Task RemoveMemberAsync(Guid teamId, Guid userId, CancellationToken cancellationToken)
    {
        // Members may leave a team themselves; removing someone else requires the project manager role.
        var required = userId == currentUser.UserId ? TeamRole.Viewer : TeamRole.ProjectManager;
        await access.EnsureRoleAsync(teamId, required, cancellationToken);

        var team = await LoadTeamAsync(teamId, cancellationToken);
        if (team.RoleOf(userId) is null)
        {
            throw new NotFoundException("TeamMember", userId);
        }

        team.RemoveMember(userId);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<Team> LoadTeamAsync(Guid teamId, CancellationToken cancellationToken) =>
        await db.Teams.Include(t => t.Members).SingleOrDefaultAsync(t => t.Id == teamId, cancellationToken)
        ?? throw new NotFoundException("Team", teamId);

    private static TeamMemberDto ToDto(TeamMember member, UserSummary? user) =>
        new(member.UserId, user?.Email ?? string.Empty, user?.DisplayName ?? string.Empty, member.Role, member.JoinedAt);
}
