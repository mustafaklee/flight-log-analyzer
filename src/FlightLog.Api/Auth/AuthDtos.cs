using FlightLog.Domain.Teams;

namespace FlightLog.Api.Auth;

public sealed record RegisterRequest(string Email, string Password, string DisplayName);

public sealed record LoginRequest(string Email, string Password);

public sealed record RefreshRequest(string RefreshToken);

public sealed record UserProfileDto(Guid Id, string Email, string DisplayName, IReadOnlyList<string> Roles, IReadOnlyList<MembershipDto> Teams);

public sealed record MembershipDto(Guid TeamId, string TeamName, TeamRole Role);
