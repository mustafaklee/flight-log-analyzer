using FlightLog.Application.Abstractions;
using FlightLog.Application.Authorization;
using FlightLog.Application.Common;
using FlightLog.Domain.Analysis;
using FlightLog.Domain.Collaboration;
using FlightLog.Domain.Common;
using FlightLog.Domain.Teams;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace FlightLog.Application.Collaboration;

public interface IFindingReviewService
{
    Task<FindingReviewDto> UpdateAsync(Guid findingId, UpdateFindingReviewRequest request, CancellationToken cancellationToken);

    /// <summary>Who changed what on the finding's review, newest first.</summary>
    Task<IReadOnlyList<AuditEntryDto>> HistoryAsync(Guid findingId, CancellationToken cancellationToken);

    /// <summary>A team's reviewed findings (work list), most recently changed first.</summary>
    Task<PagedResult<ReviewListItemDto>> ListForTeamAsync(Guid teamId, ReviewQuery query, CancellationToken cancellationToken);

    /// <summary>Reviews matching the given findings of one flight, by finding id.</summary>
    Task<IReadOnlyDictionary<Guid, FindingReviewDto>> ForFindingsAsync(Guid flightId, IReadOnlyCollection<AnalysisFinding> findings, CancellationToken cancellationToken);
}

/// <summary>
/// Finding review: status (Open, Investigating, Confirmed, Dismissed, Resolved), assignee and flag. Engineers and
/// project managers change reviews; the assignee must be an engineer or project manager of the team. Every change
/// is written to the audit trail, which is the review history.
/// </summary>
internal sealed class FindingReviewService(
    IApplicationDbContext db,
    ITeamAccessService access,
    ICurrentUser currentUser,
    IUserDirectory users,
    IAuditTrail audit,
    TimeProvider clock,
    IValidator<UpdateFindingReviewRequest> updateValidator,
    IValidator<ReviewQuery> queryValidator) : IFindingReviewService
{
    public async Task<FindingReviewDto> UpdateAsync(Guid findingId, UpdateFindingReviewRequest request, CancellationToken cancellationToken)
    {
        await updateValidator.EnsureValidAsync(request, cancellationToken);
        var finding = await db.AnalysisFindings.AsNoTracking().SingleOrDefaultAsync(f => f.Id == findingId, cancellationToken)
            ?? throw new NotFoundException("Finding", findingId);
        var teamId = await db.Flights.Where(f => f.Id == finding.FlightId).Select(f => f.TeamId).SingleAsync(cancellationToken);
        await access.EnsureResourceRoleAsync(teamId, TeamRole.Engineer, "Finding", findingId, cancellationToken);

        var now = clock.GetUtcNow();
        var userId = currentUser.UserId;
        var review = await FindReviewAsync(db, finding, cancellationToken, tracked: true);
        if (review is null)
        {
            review = FindingReview.Start(teamId, finding, userId, now);
            db.FindingReviews.Add(review);
        }

        var label = $"{finding.Title} ({finding.RuleId})";
        var previousStatus = review.Status;
        if (request.Status is { } status && review.ChangeStatus(status, userId, now))
        {
            audit.Record(teamId, AuditActions.FindingStatusChanged, AuditEntities.FindingReview, review.Id, finding.FlightId,
                $"{label}: {previousStatus} → {status}", new Dictionary<string, string?> { ["from"] = previousStatus.ToString(), ["to"] = status.ToString() });
        }

        if (request.Unassign || request.AssigneeId is not null)
        {
            var assignee = request.Unassign ? null : request.AssigneeId;
            if (assignee is { } id)
            {
                await EnsureAssignableAsync(teamId, id, cancellationToken);
            }

            var previous = review.AssigneeId;
            if (review.Assign(assignee, userId, now))
            {
                // Names are recorded as they are now, so the history stays readable if a user is renamed or removed.
                var name = await UserNames.ResolveAsync(users, new[] { previous, assignee }.OfType<Guid>(), cancellationToken);
                var fromName = previous is { } p ? name(p).DisplayName : null;
                var toName = assignee is { } a ? name(a).DisplayName : null;
                audit.Record(teamId, AuditActions.FindingAssigned, AuditEntities.FindingReview, review.Id, finding.FlightId,
                    $"{label}: {fromName ?? "-"} → {toName ?? "-"}",
                    new Dictionary<string, string?>
                    {
                        ["from"] = previous?.ToString(),
                        ["to"] = assignee?.ToString(),
                        ["fromName"] = fromName,
                        ["toName"] = toName,
                    });
            }
        }

        if (request.Flagged is { } flagged && review.SetFlag(flagged, userId, now))
        {
            audit.Record(teamId, flagged ? AuditActions.FindingFlagged : AuditActions.FindingUnflagged, AuditEntities.FindingReview, review.Id,
                finding.FlightId, label);
        }

        if (!string.IsNullOrWhiteSpace(request.Note))
        {
            var comment = FlightComment.Create(teamId, finding.FlightId, review.Id, userId, request.Note, null, null, now);
            db.FlightComments.Add(comment);
            audit.Record(teamId, AuditActions.CommentCreated, AuditEntities.Comment, comment.Id, finding.FlightId, CommentService.Excerpt(comment.Body),
                new Dictionary<string, string?> { ["findingReviewId"] = review.Id.ToString() });
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("review.version_mismatch", "The review was changed by someone else at the same time. Reload and try again.");
        }
        catch (DbUpdateException) when (db.ChangeTracker.Entries<FindingReview>().Any(e => e.State == EntityState.Added))
        {
            // Two first reviews of the same finding at once: the unique key let one in.
            throw new ConflictException("review.version_mismatch", "The finding was reviewed by someone else at the same time. Reload and try again.");
        }

        return (await ToDtosAsync([review], cancellationToken))[review.Id];
    }

    public async Task<IReadOnlyList<AuditEntryDto>> HistoryAsync(Guid findingId, CancellationToken cancellationToken)
    {
        var finding = await LoadFindingAsync(findingId, TeamRole.Viewer, cancellationToken);
        var review = await FindReviewAsync(db, finding, cancellationToken);
        if (review is null)
        {
            return [];
        }

        var commentIds = db.FlightComments.Where(c => c.FindingReviewId == review.Id).Select(c => c.Id);
        var entries = await db.AuditEntries.AsNoTracking()
            .Where(a => (a.EntityType == AuditEntities.FindingReview && a.EntityId == review.Id)
                || (a.EntityType == AuditEntities.Comment && commentIds.Contains(a.EntityId)))
            .OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id)
            .Take(500)
            .ToListAsync(cancellationToken);
        return await AuditLogService.ToDtosAsync(users, entries, cancellationToken);
    }

    public async Task<PagedResult<ReviewListItemDto>> ListForTeamAsync(Guid teamId, ReviewQuery query, CancellationToken cancellationToken)
    {
        await queryValidator.EnsureValidAsync(query, cancellationToken);
        await access.EnsureRoleAsync(teamId, TeamRole.Viewer, cancellationToken);
        var paging = new PageRequest(query.Page, query.PageSize);

        var reviews = db.FindingReviews.AsNoTracking().Where(r => r.TeamId == teamId);
        if (query.AssigneeId is { } assignee)
        {
            reviews = reviews.Where(r => r.AssigneeId == assignee);
        }

        if (query.Status is { } status)
        {
            reviews = reviews.Where(r => r.Status == status);
        }
        else if (query.OpenOnly)
        {
            reviews = reviews.Where(r => r.Status == FindingReviewStatus.Open || r.Status == FindingReviewStatus.Investigating);
        }

        if (query.Flagged is { } flagged)
        {
            reviews = reviews.Where(r => r.Flagged == flagged);
        }

        var total = await reviews.CountAsync(cancellationToken);
        var rows = await reviews.OrderByDescending(r => r.UpdatedAt).ThenByDescending(r => r.Id)
            .Skip(paging.Skip).Take(paging.SafePageSize)
            .Select(r => new
            {
                Review = r,
                Flight = db.Flights.Where(f => f.Id == r.FlightId).Select(f => new { f.Name, f.LogStartTimestampUs }).First(),
                Finding = db.AnalysisFindings
                    .Where(f => f.FlightId == r.FlightId && f.RuleId == r.RuleId && f.StartUs == r.StartUs && f.EndUs == r.EndUs
                        && f.RunId == db.AnalysisRuns.Where(a => a.FlightId == r.FlightId && a.Status == AnalysisRunStatus.Succeeded)
                            .OrderByDescending(a => a.FinishedAt).Select(a => a.Id).FirstOrDefault())
                    .Select(f => new { f.Id, f.Title, f.Severity })
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        var name = await UserNames.ResolveAsync(users, rows.Where(r => r.Review.AssigneeId is not null).Select(r => r.Review.AssigneeId!.Value), cancellationToken);
        var items = rows.Select(r => new ReviewListItemDto(
                r.Review.Id, r.Review.FlightId, r.Flight.Name, r.Review.RuleId, r.Review.StartUs, r.Review.EndUs, r.Flight.LogStartTimestampUs,
                r.Review.Status, r.Review.AssigneeId is { } a ? name(a) : null, r.Review.Flagged, r.Review.UpdatedAt,
                r.Finding?.Id, r.Finding?.Title, r.Finding?.Severity))
            .ToList();
        return new PagedResult<ReviewListItemDto>(items, paging.SafePage, paging.SafePageSize, total);
    }

    public async Task<IReadOnlyDictionary<Guid, FindingReviewDto>> ForFindingsAsync(
        Guid flightId, IReadOnlyCollection<AnalysisFinding> findings, CancellationToken cancellationToken)
    {
        if (findings.Count == 0)
        {
            return new Dictionary<Guid, FindingReviewDto>();
        }

        var reviews = await db.FindingReviews.AsNoTracking().Where(r => r.FlightId == flightId).ToListAsync(cancellationToken);
        if (reviews.Count == 0)
        {
            return new Dictionary<Guid, FindingReviewDto>();
        }

        var dtos = await ToDtosAsync(reviews, cancellationToken);
        var byKey = reviews.ToDictionary(r => (r.RuleId, r.StartUs, r.EndUs));
        var result = new Dictionary<Guid, FindingReviewDto>();
        foreach (var finding in findings)
        {
            if (byKey.TryGetValue((finding.RuleId, finding.StartUs, finding.EndUs), out var review))
            {
                result[finding.Id] = dtos[review.Id];
            }
        }

        return result;
    }

    internal static Task<FindingReview?> FindReviewAsync(IApplicationDbContext db, AnalysisFinding finding, CancellationToken cancellationToken, bool tracked = false)
    {
        var reviews = tracked ? db.FindingReviews : db.FindingReviews.AsNoTracking();
        return reviews.SingleOrDefaultAsync(
            r => r.FlightId == finding.FlightId && r.RuleId == finding.RuleId && r.StartUs == finding.StartUs && r.EndUs == finding.EndUs,
            cancellationToken);
    }

    private async Task<AnalysisFinding> LoadFindingAsync(Guid findingId, TeamRole required, CancellationToken cancellationToken)
    {
        var finding = await db.AnalysisFindings.AsNoTracking().SingleOrDefaultAsync(f => f.Id == findingId, cancellationToken)
            ?? throw new NotFoundException("Finding", findingId);
        var teamId = await db.Flights.Where(f => f.Id == finding.FlightId).Select(f => f.TeamId).SingleAsync(cancellationToken);
        await access.EnsureResourceRoleAsync(teamId, required, "Finding", findingId, cancellationToken);
        return finding;
    }

    private async Task EnsureAssignableAsync(Guid teamId, Guid userId, CancellationToken cancellationToken)
    {
        var role = await db.TeamMembers.Where(m => m.TeamId == teamId && m.UserId == userId).Select(m => (TeamRole?)m.Role).SingleOrDefaultAsync(cancellationToken);
        if (role is null)
        {
            throw new DomainRuleViolationException("review.assignee_not_member", "The assignee must be a member of the flight's team.");
        }

        if (!role.Value.Includes(TeamRole.Engineer))
        {
            throw new DomainRuleViolationException("review.assignee_read_only", "Viewers cannot be assigned findings; assign an engineer or project manager.");
        }
    }

    private async Task<Dictionary<Guid, FindingReviewDto>> ToDtosAsync(IReadOnlyCollection<FindingReview> reviews, CancellationToken cancellationToken)
    {
        var ids = reviews.Select(r => r.Id).ToList();
        var counts = await db.FlightComments.AsNoTracking()
            .Where(c => c.FindingReviewId != null && ids.Contains(c.FindingReviewId.Value) && c.DeletedAt == null)
            .GroupBy(c => c.FindingReviewId!.Value)
            .Select(g => new { Id = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Id, g => g.Count, cancellationToken);
        var name = await UserNames.ResolveAsync(
            users, reviews.Select(r => r.UpdatedBy).Concat(reviews.Where(r => r.AssigneeId is not null).Select(r => r.AssigneeId!.Value)), cancellationToken);
        return reviews.ToDictionary(r => r.Id, r => new FindingReviewDto(
            r.Id, r.Status, r.AssigneeId is { } a ? name(a) : null, r.Flagged, name(r.UpdatedBy), r.UpdatedAt, counts.GetValueOrDefault(r.Id)));
    }
}

internal sealed class UpdateFindingReviewRequestValidator : AbstractValidator<UpdateFindingReviewRequest>
{
    public UpdateFindingReviewRequestValidator()
    {
        RuleFor(x => x.Status).IsInEnum().When(x => x.Status is not null);
        RuleFor(x => x.Note).MaximumLength(FlightComment.BodyMaxLength);
        RuleFor(x => x.AssigneeId).Null().When(x => x.Unassign).WithMessage("Give either 'assigneeId' or 'unassign', not both.");
        RuleFor(x => x).Must(x => x.Status is not null || x.AssigneeId is not null || x.Unassign || x.Flagged is not null || !string.IsNullOrWhiteSpace(x.Note))
            .WithName("request").WithMessage("Nothing to change.");
    }
}

internal sealed class ReviewQueryValidator : AbstractValidator<ReviewQuery>
{
    public ReviewQueryValidator()
    {
        RuleFor(x => x.Status).IsInEnum().When(x => x.Status is not null);
        RuleFor(x => x.PageSize).InclusiveBetween(1, ReviewQuery.MaxPageSize);
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
    }
}
