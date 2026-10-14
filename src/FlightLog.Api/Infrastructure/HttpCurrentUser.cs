using System.Security.Claims;
using FlightLog.Application.Abstractions;
using FlightLog.Infrastructure.Identity;

namespace FlightLog.Api.Infrastructure;

internal sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal Principal =>
        accessor.HttpContext?.User ?? throw new InvalidOperationException("No HTTP context is available.");

    public Guid UserId =>
        Guid.TryParse(Principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? id
            : throw new InvalidOperationException("The current request is not authenticated.");

    public bool IsAdministrator => Principal.IsInRole(SystemRoles.Administrator);
}
