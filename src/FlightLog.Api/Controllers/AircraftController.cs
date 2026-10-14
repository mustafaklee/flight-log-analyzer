using FlightLog.Application.Common;
using FlightLog.Application.Fleet;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FlightLog.Api.Controllers;

/// <summary>Fleet management. Engineers create and edit aircraft; project managers delete them.</summary>
[ApiController]
[Authorize]
[Route("api/v1")]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
public sealed class AircraftController(IAircraftService aircraft) : ControllerBase
{
    [HttpGet("teams/{teamId:guid}/aircraft")]
    public Task<PagedResult<AircraftDto>> List(Guid teamId, [FromQuery] AircraftQuery query, CancellationToken cancellationToken) =>
        aircraft.ListAsync(teamId, query, cancellationToken);

    [HttpPost("teams/{teamId:guid}/aircraft")]
    [ProducesResponseType<AircraftDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AircraftDto>> Create(Guid teamId, SaveAircraftRequest request, CancellationToken cancellationToken)
    {
        var created = await aircraft.CreateAsync(teamId, request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { aircraftId = created.Id }, created);
    }

    [HttpGet("aircraft/{aircraftId:guid}")]
    public Task<AircraftDto> Get(Guid aircraftId, CancellationToken cancellationToken) =>
        aircraft.GetAsync(aircraftId, cancellationToken);

    /// <summary>Replaces the aircraft details. <c>version</c> must be the version last read.</summary>
    [HttpPut("aircraft/{aircraftId:guid}")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<AircraftDto> Update(Guid aircraftId, SaveAircraftRequest request, CancellationToken cancellationToken) =>
        aircraft.UpdateAsync(aircraftId, request, cancellationToken);

    [HttpDelete("aircraft/{aircraftId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Delete(Guid aircraftId, CancellationToken cancellationToken)
    {
        await aircraft.DeleteAsync(aircraftId, cancellationToken);
        return NoContent();
    }
}
