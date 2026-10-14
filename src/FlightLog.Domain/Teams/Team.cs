using FlightLog.Domain.Common;

namespace FlightLog.Domain.Teams;

/// <summary>
/// A team owns projects, aircraft and flights. Membership and roles are managed through this aggregate
/// so that its invariants (no duplicate members, at least one project manager) always hold.
/// </summary>
public sealed class Team
{
    public const int NameMaxLength = 100;

    private readonly List<TeamMember> _members = [];

    private Team()
    {
        Name = null!;
    }

    private Team(Guid id, string name, Guid createdBy, DateTimeOffset createdAt)
    {
        Id = id;
        Name = name;
        CreatedBy = createdBy;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; }

    public Guid CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public IReadOnlyCollection<TeamMember> Members => _members.AsReadOnly();

    /// <summary>Creates a team; the creator becomes its first project manager.</summary>
    public static Team Create(string name, Guid creatorUserId, DateTimeOffset now)
    {
        Guard.NotEmpty(creatorUserId, "creatorUserId");
        var team = new Team(Guid.CreateVersion7(now), Guard.RequiredText(name, NameMaxLength, "name"), creatorUserId, now);
        team._members.Add(new TeamMember(team.Id, creatorUserId, TeamRole.ProjectManager, now));
        return team;
    }

    public void Rename(string name) => Name = Guard.RequiredText(name, NameMaxLength, "name");

    public TeamMember AddMember(Guid userId, TeamRole role, DateTimeOffset now)
    {
        Guard.NotEmpty(userId, "userId");
        EnsureDefined(role);
        if (_members.Any(m => m.UserId == userId))
        {
            throw new DomainRuleViolationException("team.member_exists", "The user is already a member of this team.");
        }

        var member = new TeamMember(Id, userId, role, now);
        _members.Add(member);
        return member;
    }

    public void ChangeRole(Guid userId, TeamRole role)
    {
        EnsureDefined(role);
        var member = GetMember(userId);
        if (member.Role == TeamRole.ProjectManager && role != TeamRole.ProjectManager)
        {
            EnsureAnotherProjectManager(userId);
        }

        member.Role = role;
    }

    public void RemoveMember(Guid userId)
    {
        var member = GetMember(userId);
        if (member.Role == TeamRole.ProjectManager)
        {
            EnsureAnotherProjectManager(userId);
        }

        _members.Remove(member);
    }

    public TeamRole? RoleOf(Guid userId) => _members.FirstOrDefault(m => m.UserId == userId)?.Role;

    private TeamMember GetMember(Guid userId) =>
        _members.FirstOrDefault(m => m.UserId == userId)
        ?? throw new DomainRuleViolationException("team.member_not_found", "The user is not a member of this team.");

    private void EnsureAnotherProjectManager(Guid exceptUserId)
    {
        if (!_members.Any(m => m.UserId != exceptUserId && m.Role == TeamRole.ProjectManager))
        {
            throw new DomainRuleViolationException("team.last_project_manager", "A team must keep at least one project manager.");
        }
    }

    private static void EnsureDefined(TeamRole role)
    {
        if (!Enum.IsDefined(role))
        {
            throw new DomainRuleViolationException("team.invalid_role", "Unknown team role.");
        }
    }
}
