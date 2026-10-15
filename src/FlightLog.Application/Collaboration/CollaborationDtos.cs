using FlightLog.Application.Common;
using FlightLog.Domain.Analysis;
using FlightLog.Domain.Collaboration;

namespace FlightLog.Application.Collaboration;

/// <summary>A user as other team members see them: id and display name (no e-mail).</summary>
public sealed record UserRefDto(Guid Id, string DisplayName);

public sealed record CommentDto(
    Guid Id,
    Guid FlightId,
    Guid? FindingReviewId,
    UserRefDto Author,
    string? Body,
    long? StartUs,
    long? EndUs,
    DateTimeOffset CreatedAt,
    DateTimeOffset? EditedAt,
    bool Deleted,
    bool CanEdit,
    bool CanDelete,
    uint Version);

/// <summary>A new comment. A time range (log µs) makes it a note on part of the flight; a finding id puts it on that finding.</summary>
public sealed record CreateCommentRequest(string Body, long? StartUs = null, long? EndUs = null, Guid? FindingId = null);

public sealed record EditCommentRequest(string Body, uint Version);

public sealed record FindingReviewDto(
    Guid Id,
    FindingReviewStatus Status,
    UserRefDto? Assignee,
    bool Flagged,
    UserRefDto UpdatedBy,
    DateTimeOffset UpdatedAt,
    int CommentCount);

/// <summary>
/// Changes to a finding's review; omitted fields stay as they are. <see cref="Unassign"/> clears the assignee.
/// <see cref="Note"/> is added as a comment on the finding (for example why it was dismissed).
/// </summary>
public sealed record UpdateFindingReviewRequest(
    FindingReviewStatus? Status = null,
    Guid? AssigneeId = null,
    bool Unassign = false,
    bool? Flagged = null,
    string? Note = null);

public sealed record AuditEntryDto(
    Guid Id,
    string Action,
    string EntityType,
    Guid EntityId,
    Guid? FlightId,
    UserRefDto Actor,
    string? Summary,
    IReadOnlyDictionary<string, string?> Data,
    string CorrelationId,
    DateTimeOffset CreatedAt);

public sealed record AuditQuery(
    string? Action = null,
    string? EntityType = null,
    Guid? EntityId = null,
    Guid? FlightId = null,
    Guid? ActorId = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    int Page = 1,
    int PageSize = 50)
{
    public const int MaxPageSize = PageRequest.MaxPageSize;
}

/// <summary>A reviewed finding in a team's work list, with the matching finding of the flight's latest analysis when it still exists.</summary>
public sealed record ReviewListItemDto(
    Guid ReviewId,
    Guid FlightId,
    string FlightName,
    string RuleId,
    long StartUs,
    long EndUs,
    long? LogStartUs,
    FindingReviewStatus Status,
    UserRefDto? Assignee,
    bool Flagged,
    DateTimeOffset UpdatedAt,
    Guid? FindingId,
    string? Title,
    FindingSeverity? Severity);

public sealed record ReviewQuery(
    Guid? AssigneeId = null,
    FindingReviewStatus? Status = null,
    bool? Flagged = null,
    bool OpenOnly = false,
    int Page = 1,
    int PageSize = 50)
{
    public const int MaxPageSize = PageRequest.MaxPageSize;
}
