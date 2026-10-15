using FlightLog.Application.Common;
using FlightLog.Application.Flights;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FlightLog.Api.Controllers;

/// <summary>Flight history across the teams the caller belongs to.</summary>
[ApiController]
[Authorize]
[Route("api/v1/flights")]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
public sealed class FlightsController(IFlightService flights) : ControllerBase
{
    /// <summary>
    /// Lists flights with filtering, search, sorting and paging. Dates filter on the flight start time when
    /// it is known, otherwise on the upload time.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public Task<PagedResult<FlightListItemDto>> List([FromQuery] FlightQuery query, CancellationToken cancellationToken) =>
        flights.ListAsync(query, cancellationToken);

    [HttpGet("{flightId:guid}")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<FlightDto> Get(Guid flightId, CancellationToken cancellationToken) =>
        flights.GetAsync(flightId, cancellationToken);

    /// <summary>Updates name, notes and the aircraft link. <c>version</c> must be the version last read.</summary>
    [HttpPut("{flightId:guid}")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<FlightDto> Update(Guid flightId, UpdateFlightRequest request, CancellationToken cancellationToken) =>
        flights.UpdateAsync(flightId, request, cancellationToken);

    /// <summary>Deletes the flight and its log file records (project manager only).</summary>
    [HttpDelete("{flightId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid flightId, CancellationToken cancellationToken)
    {
        await flights.DeleteAsync(flightId, cancellationToken);
        return NoContent();
    }

    /// <summary>Replaces the flight's tags (Engineer).</summary>
    [HttpPut("{flightId:guid}/tags")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public Task<FlightDto> SetTags(Guid flightId, SetFlightTagsRequest request, CancellationToken cancellationToken) =>
        flights.SetTagsAsync(flightId, request, cancellationToken);
}

/// <summary>Tags used in a team, for suggestions and filters.</summary>
[ApiController]
[Authorize]
[Route("api/v1/teams/{teamId:guid}/flight-tags")]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
public sealed class FlightTagsController(IFlightService flights) : ControllerBase
{
    /// <summary>Tags of the team's flights, most used first (at most 200).</summary>
    [HttpGet]
    public Task<IReadOnlyList<TagCountDto>> List(Guid teamId, CancellationToken cancellationToken) => flights.ListTagsAsync(teamId, cancellationToken);
}
