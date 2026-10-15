using FlightLog.Application.Analysis;
using FlightLog.Application.Common;
using FlightLog.Application.Processing;
using FlightLog.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FlightLog.Api.Controllers;

/// <summary>
/// Rule-based technical analysis of processed flights. A run is queued and executed by the worker; findings name
/// the rule (and version), the time range on the log clock, the measured values and the thresholds applied.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1")]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
public sealed class AnalysisController(IAnalysisRunService analysis) : ControllerBase
{
    /// <summary>Queues a new analysis of the flight's processed log (Engineer). Poll the returned run for its result.</summary>
    [HttpPost("flights/{flightId:guid}/analysis-runs")]
    [ProducesResponseType<AnalysisRunDto>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<AnalysisRunDto>> RequestRun(Guid flightId, CancellationToken cancellationToken)
    {
        var run = await analysis.RequestAsync(flightId, cancellationToken);
        return AcceptedAtAction(nameof(Get), new { runId = run.Id }, run);
    }

    /// <summary>Analysis runs of a flight, newest first.</summary>
    [HttpGet("flights/{flightId:guid}/analysis-runs")]
    public Task<IReadOnlyList<AnalysisRunDto>> ListForFlight(Guid flightId, CancellationToken cancellationToken) =>
        analysis.ListForFlightAsync(flightId, cancellationToken);

    /// <summary>A run with its status, counts and the outcome of every rule (applied, not applicable and why, failed).</summary>
    [HttpGet("analysis-runs/{runId:guid}")]
    public Task<AnalysisRunDto> Get(Guid runId, CancellationToken cancellationToken) => analysis.GetAsync(runId, cancellationToken);

    /// <summary>Findings of a run in time order; filter by minimum severity, category or rule.</summary>
    [HttpGet("analysis-runs/{runId:guid}/findings")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public Task<PagedResult<FindingDto>> ListFindings(Guid runId, [FromQuery] FindingQuery query, CancellationToken cancellationToken) =>
        analysis.ListFindingsAsync(runId, query, cancellationToken);

    /// <summary>The rules of the engine: what each checks, the data it needs and when it does not apply.</summary>
    [HttpGet("analysis-rules")]
    public IReadOnlyList<RuleDescriptorDto> ListRules() => analysis.ListRules();
}

/// <summary>Processing figures for administrators: queue, failures, retries, durations.</summary>
[ApiController]
[Authorize(Roles = SystemRoles.Administrator)]
[Route("api/v1/admin/processing")]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
public sealed class ProcessingStatsController(IProcessingStatsService stats) : ControllerBase
{
    /// <summary>Statistics of the last <c>hours</c> hours (1 to 720, default 24).</summary>
    [HttpGet("stats")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ProcessingStatsDto>> Get(CancellationToken cancellationToken, [FromQuery] int hours = 24)
    {
        if (hours is < 1 or > 720)
        {
            ModelState.AddModelError(nameof(hours), "Give between 1 and 720 hours.");
            return ValidationProblem(ModelState);
        }

        return await stats.GetAsync(TimeSpan.FromHours(hours), cancellationToken);
    }
}
