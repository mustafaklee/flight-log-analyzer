using FlightLog.Application.Dashboard;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FlightLog.Api.Controllers;

/// <summary>Team overview for the dashboard.</summary>
[ApiController]
[Authorize]
[Route("api/v1/teams/{teamId:guid}/dashboard")]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
public sealed class DashboardController(IDashboardService dashboard) : ControllerBase
{
    /// <summary>Counts, processing state, findings of the latest analyses and the most recent flights of the team.</summary>
    [HttpGet]
    public Task<DashboardDto> Get(Guid teamId, CancellationToken cancellationToken) => dashboard.GetAsync(teamId, cancellationToken);
}
