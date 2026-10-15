using FlightLog.Domain.Analysis;
using FlightLog.Domain.Common;

namespace FlightLog.Domain.Collaboration;

/// <summary>Where the team stands on a finding.</summary>
public enum FindingReviewStatus
{
    Open = 1,
    Investigating = 2,
    Confirmed = 3,
    Dismissed = 4,
    Resolved = 5,
}

/// <summary>
/// The team's review of a finding: status, assignee and a flag. Findings are rewritten by every analysis run, so a
/// review is keyed by what identifies the finding across runs of the deterministic rule engine: flight, rule and
/// time range. A re-analysis that finds the same thing finds its review again; a finding that disappears leaves
/// its review (and history) in place.
/// </summary>
public sealed class FindingReview
{
    private FindingReview()
    {
        RuleId = null!;
    }

    public Guid Id { get; private set; }

    public Guid TeamId { get; private set; }

    public Guid FlightId { get; private set; }

    public string RuleId { get; private set; }

    public long StartUs { get; private set; }

    public long EndUs { get; private set; }

    public FindingReviewStatus Status { get; private set; }

    public Guid? AssigneeId { get; private set; }

    /// <summary>Marked by a team member as needing attention.</summary>
    public bool Flagged { get; private set; }

    public Guid CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public Guid UpdatedBy { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public uint Version { get; private set; }

    /// <summary>A new review in state <see cref="FindingReviewStatus.Open"/> for the finding.</summary>
    public static FindingReview Start(Guid teamId, AnalysisFinding finding, Guid userId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(finding);
        return new FindingReview
        {
            Id = Guid.CreateVersion7(now),
            TeamId = Guard.NotEmpty(teamId, "teamId"),
            FlightId = finding.FlightId,
            RuleId = finding.RuleId,
            StartUs = finding.StartUs,
            EndUs = finding.EndUs,
            Status = FindingReviewStatus.Open,
            CreatedBy = Guard.NotEmpty(userId, "userId"),
            CreatedAt = now,
            UpdatedBy = userId,
            UpdatedAt = now,
        };
    }

    public bool Matches(AnalysisFinding finding) =>
        finding is not null && finding.FlightId == FlightId && finding.RuleId == RuleId && finding.StartUs == StartUs && finding.EndUs == EndUs;

    /// <summary>Changes the status; returns false when it already had it.</summary>
    public bool ChangeStatus(FindingReviewStatus status, Guid userId, DateTimeOffset now)
    {
        if (!Enum.IsDefined(status))
        {
            throw new DomainRuleViolationException("review.invalid_status", "Unknown review status.");
        }

        if (status == Status)
        {
            return false;
        }

        Status = status;
        Touch(userId, now);
        return true;
    }

    /// <summary>Assigns the finding (null clears the assignee); returns false when nothing changed.</summary>
    public bool Assign(Guid? assigneeId, Guid userId, DateTimeOffset now)
    {
        var value = assigneeId == Guid.Empty ? null : assigneeId;
        if (value == AssigneeId)
        {
            return false;
        }

        AssigneeId = value;
        Touch(userId, now);
        return true;
    }

    public bool SetFlag(bool flagged, Guid userId, DateTimeOffset now)
    {
        if (flagged == Flagged)
        {
            return false;
        }

        Flagged = flagged;
        Touch(userId, now);
        return true;
    }

    private void Touch(Guid userId, DateTimeOffset now)
    {
        UpdatedBy = Guard.NotEmpty(userId, "userId");
        UpdatedAt = now;
    }
}
