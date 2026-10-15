using FlightLog.Domain.Common;

namespace FlightLog.Domain.Collaboration;

/// <summary>
/// A team member's comment on a flight. It can be tied to a time range of the log (a note on part of the flight)
/// and to a finding's review. Deleting keeps the row without its text so that threads and the audit trail stay
/// readable.
/// </summary>
public sealed class FlightComment
{
    public const int BodyMaxLength = 4000;

    private FlightComment()
    {
    }

    public Guid Id { get; private set; }

    public Guid TeamId { get; private set; }

    public Guid FlightId { get; private set; }

    /// <summary>The finding review the comment belongs to; null for comments on the flight itself.</summary>
    public Guid? FindingReviewId { get; private set; }

    public Guid AuthorId { get; private set; }

    /// <summary>Null once deleted.</summary>
    public string? Body { get; private set; }

    /// <summary>Log time range (µs, the log's own clock) the comment is about; both null for a general comment.</summary>
    public long? StartUs { get; private set; }

    public long? EndUs { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? EditedAt { get; private set; }

    public DateTimeOffset? DeletedAt { get; private set; }

    public Guid? DeletedBy { get; private set; }

    public uint Version { get; private set; }

    public bool IsDeleted => DeletedAt is not null;

    public static FlightComment Create(
        Guid teamId, Guid flightId, Guid? findingReviewId, Guid authorId, string body, long? startUs, long? endUs, DateTimeOffset now)
    {
        var (start, end) = Range(startUs, endUs);
        return new FlightComment
        {
            Id = Guid.CreateVersion7(now),
            TeamId = Guard.NotEmpty(teamId, "teamId"),
            FlightId = Guard.NotEmpty(flightId, "flightId"),
            FindingReviewId = findingReviewId == Guid.Empty ? null : findingReviewId,
            AuthorId = Guard.NotEmpty(authorId, "authorId"),
            Body = Guard.RequiredText(body, BodyMaxLength, "body"),
            StartUs = start,
            EndUs = end,
            CreatedAt = now,
        };
    }

    public void Edit(string body, DateTimeOffset now)
    {
        EnsureNotDeleted();
        Body = Guard.RequiredText(body, BodyMaxLength, "body");
        EditedAt = now;
    }

    public void Delete(Guid userId, DateTimeOffset now)
    {
        EnsureNotDeleted();
        Body = null;
        DeletedAt = now;
        DeletedBy = Guard.NotEmpty(userId, "userId");
    }

    private void EnsureNotDeleted()
    {
        if (IsDeleted)
        {
            throw new DomainRuleViolationException("comment.deleted", "The comment has been deleted.");
        }
    }

    private static (long? Start, long? End) Range(long? startUs, long? endUs)
    {
        if (startUs is null && endUs is null)
        {
            return (null, null);
        }

        // A single point in time is a range of length zero.
        var start = startUs ?? endUs!.Value;
        var end = endUs ?? start;
        if (start < 0 || end < start)
        {
            throw new DomainRuleViolationException("comment.invalid_range", "The time range must start at or after 0 and not end before it starts.");
        }

        return (start, end);
    }
}
