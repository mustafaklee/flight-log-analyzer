using System.Text;
using FlightLog.Application.Ai;
using FlightLog.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FlightLog.Api.Controllers;

/// <summary>
/// AI reports about flights (ADR-0008). A report interprets one successful rule analysis; the worker builds the
/// context, calls the model and checks every cited reference and number against the context before storing it.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1")]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
public sealed class AiAnalysisController(IAiAnalysisAppService ai) : ControllerBase
{
    /// <summary>
    /// Queues an AI report of the flight's latest successful rule analysis (Engineer). Fails with
    /// <c>ai.disabled</c> when AI analysis is off, <c>ai.already_pending</c> while one is running and
    /// <c>ai.quota_exceeded</c> (429) when the team's daily limit is used up.
    /// </summary>
    [HttpPost("flights/{flightId:guid}/ai-analyses")]
    [ProducesResponseType<AiAnalysisDto>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<AiAnalysisDto>> RequestAnalysis(Guid flightId, CancellationToken cancellationToken)
    {
        var analysis = await ai.RequestAsync(flightId, cancellationToken);
        return AcceptedAtAction(nameof(Get), new { id = analysis.Id }, analysis);
    }

    /// <summary>AI analyses of a flight, newest first (without report body).</summary>
    [HttpGet("flights/{flightId:guid}/ai-analyses")]
    public Task<IReadOnlyList<AiAnalysisDto>> ListForFlight(Guid flightId, CancellationToken cancellationToken) =>
        ai.ListForFlightAsync(flightId, cancellationToken);

    /// <summary>An AI analysis with its validated report, usage, cost and versions.</summary>
    [HttpGet("ai-analyses/{id:guid}")]
    public Task<AiAnalysisDto> Get(Guid id, CancellationToken cancellationToken) => ai.GetAsync(id, cancellationToken);

    /// <summary>The exact context the model received (JSON; Engineer).</summary>
    [HttpGet("ai-analyses/{id:guid}/context")]
    [Produces("application/json")]
    [ProducesResponseType<string>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> GetContext(Guid id, CancellationToken cancellationToken) =>
        Content(await ai.GetContextAsync(id, cancellationToken), "application/json", Encoding.UTF8);

    /// <summary>The report as a Markdown file.</summary>
    [HttpGet("ai-analyses/{id:guid}/report.md")]
    [Produces("text/markdown")]
    [ProducesResponseType<string>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> GetMarkdown(Guid id, CancellationToken cancellationToken)
    {
        var (fileName, markdown) = await ai.GetMarkdownAsync(id, cancellationToken);
        return File(Encoding.UTF8.GetBytes(markdown), "text/markdown; charset=utf-8", fileName);
    }

    /// <summary>Whether AI analysis is enabled, with the model and the team's daily quota.</summary>
    [HttpGet("teams/{teamId:guid}/ai-status")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public Task<AiStatusDto> GetStatus(Guid teamId, CancellationToken cancellationToken) => ai.GetStatusAsync(teamId, cancellationToken);
}

/// <summary>AI usage for administrators: analyses, tokens, estimated cost, latency, failures.</summary>
[ApiController]
[Authorize(Roles = SystemRoles.Administrator)]
[Route("api/v1/admin/ai")]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
public sealed class AiUsageController(IAiUsageService usage) : ControllerBase
{
    /// <summary>Usage of the last <c>days</c> days (1 to 365, default 30).</summary>
    [HttpGet("usage")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<AiUsageDto>> Get(CancellationToken cancellationToken, [FromQuery] int days = 30)
    {
        if (days is < 1 or > 365)
        {
            ModelState.AddModelError(nameof(days), "Give between 1 and 365 days.");
            return ValidationProblem(ModelState);
        }

        return await usage.GetAsync(TimeSpan.FromDays(days), cancellationToken);
    }
}
