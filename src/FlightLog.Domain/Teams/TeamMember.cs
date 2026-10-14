namespace FlightLog.Domain.Teams;

public sealed class TeamMember
{
    private TeamMember()
    {
    }

    internal TeamMember(Guid teamId, Guid userId, TeamRole role, DateTimeOffset joinedAt)
    {
        TeamId = teamId;
        UserId = userId;
        Role = role;
        JoinedAt = joinedAt;
    }

    public Guid TeamId { get; private set; }

    public Guid UserId { get; private set; }

    public TeamRole Role { get; internal set; }

    public DateTimeOffset JoinedAt { get; private set; }
}
