using FlightLog.Application.Collaboration;
using FlightLog.Application.Common;
using FlightLog.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FlightLog.Api.Controllers;

/// <summary>
/// Team work on a flight: comments and time-range notes, finding reviews (status, assignee, flag) and their history.
/// Viewers read; engineers and project managers write. Times are microseconds on the log's clock, like findings.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1")]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
public sealed class CollaborationController(ICommentService comments, IFindingReviewService reviews, IAuditLogService auditLog) : ControllerBase
{
    /// <summary>Comments of a flight in time order (deleted ones without text); with <c>findingId</c> only those on that finding.</summary>
    [HttpGet("flights/{flightId:guid}/comments")]
    public Task<IReadOnlyList<CommentDto>> ListComments(Guid flightId, [FromQuery] Guid? findingId, CancellationToken cancellationToken) =>
        comments.ListAsync(flightId, findingId, cancellationToken);

    /// <summary>Adds a comment (Engineer). With <c>startUs</c>/<c>endUs</c> it is a note on that part of the flight; with <c>findingId</c> it goes on the finding.</summary>
    [HttpPost("flights/{flightId:guid}/comments")]
    [ProducesResponseType<CommentDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<CommentDto>> CreateComment(Guid flightId, CreateCommentRequest request, CancellationToken cancellationToken)
    {
        var comment = await comments.CreateAsync(flightId, request, cancellationToken);
        return CreatedAtAction(nameof(ListComments), new { flightId }, comment);
    }

    /// <summary>Edits a comment (its author only). <c>version</c> must be the version last read.</summary>
    [HttpPut("comments/{commentId:guid}")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public Task<CommentDto> EditComment(Guid commentId, EditCommentRequest request, CancellationToken cancellationToken) =>
        comments.EditAsync(commentId, request, cancellationToken);

    /// <summary>Deletes a comment (its author or a project manager); the comment stays in the thread without its text.</summary>
    [HttpDelete("comments/{commentId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> DeleteComment(Guid commentId, CancellationToken cancellationToken)
    {
        await comments.DeleteAsync(commentId, cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// Changes a finding's review (Engineer): status, assignee (an engineer or project manager of the team), flag, and
    /// an optional note that is added as a comment. The review survives re-analysis as long as the rule finds the same time range.
    /// </summary>
    [HttpPatch("findings/{findingId:guid}/review")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public Task<FindingReviewDto> UpdateReview(Guid findingId, UpdateFindingReviewRequest request, CancellationToken cancellationToken) =>
        reviews.UpdateAsync(findingId, request, cancellationToken);

    /// <summary>Who changed the finding's review and its comments, and when; newest first.</summary>
    [HttpGet("findings/{findingId:guid}/history")]
    public Task<IReadOnlyList<AuditEntryDto>> ReviewHistory(Guid findingId, CancellationToken cancellationToken) =>
        reviews.HistoryAsync(findingId, cancellationToken);

    /// <summary>The team's reviewed findings, most recently changed first (filter by assignee, status, flag).</summary>
    [HttpGet("teams/{teamId:guid}/finding-reviews")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public Task<PagedResult<ReviewListItemDto>> ListReviews(Guid teamId, [FromQuery] ReviewQuery query, CancellationToken cancellationToken) =>
        reviews.ListForTeamAsync(teamId, query, cancellationToken);

    /// <summary>What happened to a flight (uploads, analyses, edits, tags, comments, reviews, exports), newest first.</summary>
    [HttpGet("flights/{flightId:guid}/activity")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public Task<PagedResult<AuditEntryDto>> FlightActivity(Guid flightId, [FromQuery] AuditQuery query, CancellationToken cancellationToken) =>
        auditLog.ListForFlightAsync(flightId, query, cancellationToken);

    /// <summary>
    /// The team's audit trail (project manager). <c>action</c> matches exactly, or as a prefix when it ends with a dot
    /// (<c>finding.</c>).
    /// </summary>
    [HttpGet("teams/{teamId:guid}/audit-log")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public Task<PagedResult<AuditEntryDto>> TeamAuditLog(Guid teamId, [FromQuery] AuditQuery query, CancellationToken cancellationToken) =>
        auditLog.ListForTeamAsync(teamId, query, cancellationToken);
}

/// <summary>The whole audit trail for administrators.</summary>
[ApiController]
[Authorize(Roles = SystemRoles.Administrator)]
[Route("api/v1/admin/audit-log")]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
public sealed class AdminAuditLogController(IAuditLogService auditLog) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public Task<PagedResult<AuditEntryDto>> List([FromQuery] AuditQuery query, CancellationToken cancellationToken) =>
        auditLog.ListAllAsync(query, cancellationToken);
}
