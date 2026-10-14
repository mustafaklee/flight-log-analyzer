using FlightLog.Domain.Teams;

namespace FlightLog.Application.Teams;

public sealed record CreateTeamRequest(string Name);

public sealed record TeamDto(Guid Id, string Name, DateTimeOffset CreatedAt, TeamRole MyRole, int MemberCount);

public sealed record AddTeamMemberRequest(string Email, TeamRole Role);

public sealed record ChangeTeamMemberRoleRequest(TeamRole Role);

public sealed record TeamMemberDto(Guid UserId, string Email, string DisplayName, TeamRole Role, DateTimeOffset JoinedAt);
