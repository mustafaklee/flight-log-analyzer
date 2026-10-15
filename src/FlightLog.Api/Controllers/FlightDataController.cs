using FlightLog.Application.Common;
using FlightLog.Application.FlightData;
using FlightLog.Domain.FlightData;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FlightLog.Api.Controllers;

/// <summary>
/// What processing derived from a flight's log: signals and their values, events, parameters and processing history.
/// Times are microseconds on the log's own clock (see <c>logStartTimestampUs</c> and <c>bootTimeUtc</c> of the flight).
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1")]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
public sealed class FlightDataController(IFlightDataService data) : ControllerBase
{
    /// <summary>Lists the signals of a flight: raw fields of the log and normalized signals, with units and statistics.</summary>
    [HttpGet("flights/{flightId:guid}/signals")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public Task<IReadOnlyList<SignalDto>> ListSignals(Guid flightId, [FromQuery] SignalKind? kind, [FromQuery] string? search, CancellationToken cancellationToken) =>
        data.ListSignalsAsync(flightId, new SignalQuery(kind, search), cancellationToken);

    /// <summary>
    /// Values of up to 8 signals (<c>names</c> repeated or comma separated) in an optional time range. Long ranges are
    /// reduced to about <c>maxPoints</c> points per signal, keeping each bucket's minimum and maximum.
    /// </summary>
    [HttpGet("flights/{flightId:guid}/signals/data")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public Task<SignalDataDto> GetSignalData(
        Guid flightId,
        [FromQuery] string[] names,
        [FromQuery] long? fromUs,
        [FromQuery] long? toUs,
        CancellationToken cancellationToken,
        [FromQuery] int maxPoints = SignalDataQuery.DefaultMaxPoints)
    {
        var split = names.SelectMany(n => n.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList();
        return data.GetSignalDataAsync(flightId, new SignalDataQuery(split, fromUs, toUs, maxPoints), cancellationToken);
    }

    /// <summary>Timeline events (messages, mode changes, arming, failsafes, errors) in time order.</summary>
    [HttpGet("flights/{flightId:guid}/events")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public Task<PagedResult<FlightEventDto>> ListEvents(Guid flightId, [FromQuery] EventQuery query, CancellationToken cancellationToken) =>
        data.ListEventsAsync(flightId, query, cancellationToken);

    /// <summary>Parameters at log start, firmware defaults (when the log has them) and changes during the flight.</summary>
    [HttpGet("flights/{flightId:guid}/parameters")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public Task<IReadOnlyList<FlightParameterDto>> ListParameters(Guid flightId, [FromQuery] ParameterQuery query, CancellationToken cancellationToken) =>
        data.ListParametersAsync(flightId, query, cancellationToken);

    /// <summary>Processing attempts of a log file, newest first, with timing, parser statistics and failure details.</summary>
    [HttpGet("log-files/{logFileId:guid}/jobs")]
    public Task<IReadOnlyList<ProcessingJobDto>> ListJobs(Guid logFileId, CancellationToken cancellationToken) =>
        data.ListJobsAsync(logFileId, cancellationToken);
}
