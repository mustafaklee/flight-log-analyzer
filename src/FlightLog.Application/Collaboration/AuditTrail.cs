using FlightLog.Application.Abstractions;
using FlightLog.Domain.Collaboration;

namespace FlightLog.Application.Collaboration;

/// <summary>
/// Adds audit entries to the current unit of work; they are saved together with the change they describe, so a
/// change without its entry (or the reverse) cannot happen.
/// </summary>
public interface IAuditTrail
{
    void Record(Guid? teamId, string action, string entityType, Guid entityId, Guid? flightId, string? summary, IReadOnlyDictionary<string, string?>? data = null);
}

/// <summary>Action codes. They are part of the API (filters, exports) and therefore never renamed.</summary>
public static class AuditActions
{
    public const string FlightUpdated = "flight.updated";
    public const string FlightDeleted = "flight.deleted";
    public const string FlightTagsChanged = "flight.tags_changed";
    public const string LogUploaded = "log_file.uploaded";
    public const string LogCancelled = "log_file.cancelled";
    public const string LogRetried = "log_file.retried";
    public const string AnalysisRequested = "analysis.requested";
    public const string AiAnalysisRequested = "ai_analysis.requested";
    public const string CommentCreated = "comment.created";
    public const string CommentEdited = "comment.edited";
    public const string CommentDeleted = "comment.deleted";
    public const string FindingStatusChanged = "finding.status_changed";
    public const string FindingAssigned = "finding.assigned";
    public const string FindingFlagged = "finding.flagged";
    public const string FindingUnflagged = "finding.unflagged";
    public const string MemberAdded = "team.member_added";
    public const string MemberRoleChanged = "team.member_role_changed";
    public const string MemberRemoved = "team.member_removed";
    public const string ProjectCreated = "project.created";
    public const string ProjectDeleted = "project.deleted";
    public const string AircraftCreated = "aircraft.created";
    public const string AircraftDeleted = "aircraft.deleted";
    public const string DataExported = "data.exported";
}

public static class AuditEntities
{
    public const string Flight = "Flight";
    public const string LogFile = "LogFile";
    public const string AnalysisRun = "AnalysisRun";
    public const string AiAnalysis = "AiAnalysis";
    public const string Comment = "Comment";
    public const string FindingReview = "FindingReview";
    public const string Team = "Team";
    public const string Project = "Project";
    public const string Aircraft = "Aircraft";
}

internal sealed class AuditTrail(IApplicationDbContext db, ICurrentUser currentUser, TimeProvider clock) : IAuditTrail
{
    public void Record(Guid? teamId, string action, string entityType, Guid entityId, Guid? flightId, string? summary, IReadOnlyDictionary<string, string?>? data = null)
    {
        var now = clock.GetUtcNow();
        db.AuditEntries.Add(new AuditEntry
        {
            Id = Guid.CreateVersion7(now),
            TeamId = teamId,
            ActorId = currentUser.UserId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            FlightId = flightId,
            Summary = Shorten(summary, AuditEntry.SummaryMaxLength),
            Data = data is null
                ? new Dictionary<string, string?>()
                : data.ToDictionary(p => p.Key, p => Shorten(p.Value, AuditEntry.SummaryMaxLength), StringComparer.Ordinal),
            CorrelationId = Shorten(currentUser.CorrelationId, 100) ?? "",
            CreatedAt = now,
        });
    }

    private static string? Shorten(string? value, int max) => value is null || value.Length <= max ? value : value[..(max - 1)] + "…";
}
