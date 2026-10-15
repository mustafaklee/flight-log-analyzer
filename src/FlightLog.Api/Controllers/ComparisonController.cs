using FlightLog.Application.Comparison;
using FlightLog.Application.Exports;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FlightLog.Api.Controllers;

/// <summary>Flight comparison and exports (CSV, JSON). Any team member can compare and export flights they can read.</summary>
[ApiController]
[Authorize]
[Route("api/v1")]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
public sealed class ComparisonController(IComparisonService comparisons, IExportService exports) : ControllerBase
{
    /// <summary>
    /// Compares 2 to 6 flights (<c>ids</c> repeated): durations, findings and rule results, statistics of normalized
    /// signals, battery, vibration, flight modes and the latest AI report; with notes on what is not directly comparable.
    /// </summary>
    [HttpGet("flights/compare")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public Task<FlightComparisonDto> Compare([FromQuery] Guid[] ids, CancellationToken cancellationToken) =>
        comparisons.CompareAsync(new CompareFlightsRequest(ids ?? []), cancellationToken);

    /// <summary>The comparison as a CSV (one column per flight) or JSON file.</summary>
    [HttpGet("flights/compare/export")]
    [Produces("text/csv", "application/json")]
    [ProducesResponseType<string>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ExportComparison([FromQuery] Guid[] ids, CancellationToken cancellationToken, [FromQuery] ExportFormat format = ExportFormat.Csv) =>
        Download(await exports.ComparisonAsync(new CompareFlightsRequest(ids ?? []), format, cancellationToken));

    /// <summary>The flight as one JSON report: summary, analysis with findings and reviews, events, parameter changes, AI report, comments.</summary>
    [HttpGet("flights/{flightId:guid}/export/report.json")]
    [Produces("application/json")]
    [ProducesResponseType<string>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ExportReport(Guid flightId, CancellationToken cancellationToken) =>
        Download(await exports.FlightReportJsonAsync(flightId, cancellationToken));

    /// <summary>Findings of the latest analysis with review state, as CSV.</summary>
    [HttpGet("flights/{flightId:guid}/export/findings.csv")]
    [Produces("text/csv")]
    [ProducesResponseType<string>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ExportFindings(Guid flightId, CancellationToken cancellationToken) =>
        Download(await exports.FindingsCsvAsync(flightId, cancellationToken));

    /// <summary>All events of the flight as CSV.</summary>
    [HttpGet("flights/{flightId:guid}/export/events.csv")]
    [Produces("text/csv")]
    [ProducesResponseType<string>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ExportEvents(Guid flightId, CancellationToken cancellationToken) =>
        Download(await exports.EventsCsvAsync(flightId, cancellationToken));

    private FileContentResult Download(ExportFile file) =>
        File(file.Content, file.ContentType + (file.ContentType.StartsWith("text/", StringComparison.Ordinal) ? "; charset=utf-8" : ""), file.FileName);
}
