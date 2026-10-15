using FlightLog.Application.Abstractions;
using FlightLog.Application.Authorization;
using FlightLog.Application.Common;
using FlightLog.Domain.Collaboration;
using FlightLog.Domain.Teams;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace FlightLog.Application.Collaboration;

public interface ICommentService
{
    /// <summary>Comments of a flight in time order; with <paramref name="findingId"/> only those on that finding.</summary>
    Task<IReadOnlyList<CommentDto>> ListAsync(Guid flightId, Guid? findingId, CancellationToken cancellationToken);

    Task<CommentDto> CreateAsync(Guid flightId, CreateCommentRequest request, CancellationToken cancellationToken);

    Task<CommentDto> EditAsync(Guid commentId, EditCommentRequest request, CancellationToken cancellationToken);

    Task DeleteAsync(Guid commentId, CancellationToken cancellationToken);
}

/// <summary>
/// Comments and time-range notes. Viewers read; engineers write; authors edit and delete their own comments;
/// project managers may delete any comment of their team (moderation).
/// </summary>
internal sealed class CommentService(
    IApplicationDbContext db,
    ITeamAccessService access,
    ICurrentUser currentUser,
    IUserDirectory users,
    IAuditTrail audit,
    TimeProvider clock,
    IValidator<CreateCommentRequest> createValidator,
    IValidator<EditCommentRequest> editValidator) : ICommentService
{
    public async Task<IReadOnlyList<CommentDto>> ListAsync(Guid flightId, Guid? findingId, CancellationToken cancellationToken)
    {
        var (_, role) = await FlightAccess.EnsureAsync(db, access, flightId, TeamRole.Viewer, cancellationToken);
        var comments = db.FlightComments.AsNoTracking().Where(c => c.FlightId == flightId);
        if (findingId is { } id)
        {
            var finding = await FlightAccess.FindingOfFlightAsync(db, flightId, id, cancellationToken);
            var review = await FindingReviewService.FindReviewAsync(db, finding, cancellationToken);
            if (review is null)
            {
                return [];
            }

            comments = comments.Where(c => c.FindingReviewId == review.Id);
        }

        var list = await comments.OrderBy(c => c.CreatedAt).ThenBy(c => c.Id).ToListAsync(cancellationToken);
        var name = await UserNames.ResolveAsync(users, list.Select(c => c.AuthorId), cancellationToken);
        return [.. list.Select(c => ToDto(c, role, name))];
    }

    public async Task<CommentDto> CreateAsync(Guid flightId, CreateCommentRequest request, CancellationToken cancellationToken)
    {
        await createValidator.EnsureValidAsync(request, cancellationToken);
        var (teamId, role) = await FlightAccess.EnsureAsync(db, access, flightId, TeamRole.Engineer, cancellationToken);
        var now = clock.GetUtcNow();

        Guid? reviewId = null;
        if (request.FindingId is { } findingId)
        {
            var finding = await FlightAccess.FindingOfFlightAsync(db, flightId, findingId, cancellationToken);
            var review = await FindingReviewService.FindReviewAsync(db, finding, cancellationToken, tracked: true);
            if (review is null)
            {
                review = FindingReview.Start(teamId, finding, currentUser.UserId, now);
                db.FindingReviews.Add(review);
            }

            reviewId = review.Id;
        }

        var comment = FlightComment.Create(teamId, flightId, reviewId, currentUser.UserId, request.Body, request.StartUs, request.EndUs, now);
        db.FlightComments.Add(comment);
        audit.Record(teamId, AuditActions.CommentCreated, AuditEntities.Comment, comment.Id, flightId, Excerpt(comment.Body),
            new Dictionary<string, string?> { ["findingReviewId"] = reviewId?.ToString(), ["startUs"] = Invariant(comment.StartUs), ["endUs"] = Invariant(comment.EndUs) });
        await SaveAsync(cancellationToken);

        var name = await UserNames.ResolveAsync(users, [comment.AuthorId], cancellationToken);
        return ToDto(comment, role, name);
    }

    public async Task<CommentDto> EditAsync(Guid commentId, EditCommentRequest request, CancellationToken cancellationToken)
    {
        await editValidator.EnsureValidAsync(request, cancellationToken);
        var comment = await db.FlightComments.SingleOrDefaultAsync(c => c.Id == commentId, cancellationToken)
            ?? throw new NotFoundException("Comment", commentId);
        var role = await access.EnsureResourceRoleAsync(comment.TeamId, TeamRole.Engineer, "Comment", commentId, cancellationToken);
        if (comment.AuthorId != currentUser.UserId)
        {
            throw new ForbiddenException("Only the author can edit a comment.");
        }

        if (comment.Version != request.Version)
        {
            throw StaleVersion();
        }

        var before = comment.Body;
        comment.Edit(request.Body, clock.GetUtcNow());
        audit.Record(comment.TeamId, AuditActions.CommentEdited, AuditEntities.Comment, comment.Id, comment.FlightId, Excerpt(comment.Body),
            new Dictionary<string, string?> { ["from"] = before, ["to"] = comment.Body });
        await SaveAsync(cancellationToken);

        var name = await UserNames.ResolveAsync(users, [comment.AuthorId], cancellationToken);
        return ToDto(comment, role, name);
    }

    public async Task DeleteAsync(Guid commentId, CancellationToken cancellationToken)
    {
        var comment = await db.FlightComments.SingleOrDefaultAsync(c => c.Id == commentId, cancellationToken)
            ?? throw new NotFoundException("Comment", commentId);
        var role = await access.EnsureResourceRoleAsync(comment.TeamId, TeamRole.Engineer, "Comment", commentId, cancellationToken);
        if (comment.AuthorId != currentUser.UserId && !role.Includes(TeamRole.ProjectManager))
        {
            throw new ForbiddenException("Only the author or a project manager can delete a comment.");
        }

        if (comment.IsDeleted)
        {
            return;
        }

        var body = comment.Body;
        comment.Delete(currentUser.UserId, clock.GetUtcNow());

        // The deleted text stays in the audit trail so that moderation can be reviewed; it ages out with the trail.
        audit.Record(comment.TeamId, AuditActions.CommentDeleted, AuditEntities.Comment, comment.Id, comment.FlightId, Excerpt(body),
            new Dictionary<string, string?> { ["body"] = body, ["authorId"] = comment.AuthorId.ToString() });
        await SaveAsync(cancellationToken);
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw StaleVersion();
        }
    }

    private CommentDto ToDto(FlightComment c, TeamRole role, Func<Guid, UserRefDto> name)
    {
        var own = c.AuthorId == currentUser.UserId;
        var canWrite = role.Includes(TeamRole.Engineer) && !c.IsDeleted;
        return new CommentDto(
            c.Id, c.FlightId, c.FindingReviewId, name(c.AuthorId), c.Body, c.StartUs, c.EndUs, c.CreatedAt, c.EditedAt, c.IsDeleted,
            canWrite && own, canWrite && (own || role.Includes(TeamRole.ProjectManager)), c.Version);
    }

    private static string? Invariant(long? value) => value?.ToString(System.Globalization.CultureInfo.InvariantCulture);

    internal static string? Excerpt(string? body) => body is null ? null : body.Length <= 120 ? body : body[..119] + "…";

    private static ConflictException StaleVersion() =>
        new("comment.version_mismatch", "The comment was changed by someone else. Reload it and try again.");
}

internal sealed class CreateCommentRequestValidator : AbstractValidator<CreateCommentRequest>
{
    public CreateCommentRequestValidator()
    {
        RuleFor(x => x.Body).NotEmpty().MaximumLength(FlightComment.BodyMaxLength);
        RuleFor(x => x.StartUs).GreaterThanOrEqualTo(0).When(x => x.StartUs is not null);
        RuleFor(x => x.EndUs).GreaterThanOrEqualTo(x => x.StartUs ?? 0).When(x => x.EndUs is not null)
            .WithMessage("'endUs' must not be earlier than 'startUs'.");
    }
}

internal sealed class EditCommentRequestValidator : AbstractValidator<EditCommentRequest>
{
    public EditCommentRequestValidator()
    {
        RuleFor(x => x.Body).NotEmpty().MaximumLength(FlightComment.BodyMaxLength);
    }
}
