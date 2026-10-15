using System.Security.Claims;
using FlightLog.Application.Abstractions;
using FlightLog.Infrastructure.Identity;
using FlightLog.Infrastructure.Messaging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace FlightLog.Api.Realtime;

/// <summary>Messages the server sends to connected browsers.</summary>
public interface IProcessingClient
{
    /// <summary>A log file or analysis run of one of the user's teams changed status. Reload the details from the API.</summary>
    Task StatusChanged(StatusNotification notification);
}

/// <summary>
/// Pushes processing status changes to the browser (ADR-0011). Server-to-client only; on connect the user joins the
/// groups of the teams they belong to (administrators: every team).
/// </summary>
[Authorize]
public sealed class ProcessingHub(IApplicationDbContext db) : Hub<IProcessingClient>
{
    public const string Path = "/hubs/processing";
    public const string AdministratorsGroup = "administrators";

    public static string TeamGroup(Guid teamId) => $"team:{teamId:N}";

    public override async Task OnConnectedAsync()
    {
        // The hub's user comes from the connection (Context.User); there is no current HTTP request to read it from.
        var user = Context.User!;
        if (user.IsInRole(SystemRoles.Administrator))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, AdministratorsGroup, Context.ConnectionAborted);
        }
        else if (Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            var teams = await db.TeamMembers.AsNoTracking().Where(m => m.UserId == userId).Select(m => m.TeamId).ToListAsync(Context.ConnectionAborted);
            foreach (var teamId in teams)
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, TeamGroup(teamId), Context.ConnectionAborted);
            }
        }

        await base.OnConnectedAsync();
    }
}
