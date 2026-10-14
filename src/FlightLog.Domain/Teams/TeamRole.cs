namespace FlightLog.Domain.Teams;

/// <summary>
/// Role of a user inside a team. Values are ordered: a higher value includes the permissions of lower ones.
/// </summary>
public enum TeamRole
{
    Viewer = 1,
    Engineer = 2,
    ProjectManager = 3,
}

public static class TeamRoleExtensions
{
    public static bool Includes(this TeamRole role, TeamRole required) => role >= required;
}
