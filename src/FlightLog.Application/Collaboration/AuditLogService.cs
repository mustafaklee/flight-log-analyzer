using FlightLog.Application.Abstractions;
using FlightLog.Application.Authorization;
using FlightLog.Application.Common;
using FlightLog.Domain.Collaboration;
using FlightLog.Domain.Teams;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace FlightLog.Application.Collaboration;

public interface IAuditLogService
{
    /// <summary>A team's audit trail; project managers (and administrators) only.</summary>
    Task<PagedResult<AuditEntryDto>> ListForTeamAsync(Guid teamId, AuditQuery query, CancellationToken cancellationToken);

    /// <summary>What happened to a flight (comments, reviews, tags, analyses), newest first; every team member can read it.</summary>
    Task<PagedResult<AuditEntryDto>> ListForFlightAsync(Guid flightId, AuditQuery query, CancellationToken cancellationToken);

    /// <summary>The whole trail including actions outside teams; administrators only (checked by the API policy).</summary>
    Task<PagedResult<AuditEntryDto>> ListAllAsync(AuditQuery query, CancellationToken cancellationToken);
}

internal sealed class AuditLogService(
    IApplicationDbContext db, ITeamAccessService access, IUserDirectory users, IValidator<AuditQuery> validator) : IAuditLogService
{
    public async Task<PagedResult<AuditEntryDto>> ListForTeamAsync(Guid teamId, AuditQuery query, CancellationToken cancellationToken)
    {
        await validator.EnsureValidAsync(query, cancellationToken);
        await access.EnsureRoleAsync(teamId, TeamRole.ProjectManager, cancellationToken);
        return await PageAsync(db.AuditEntries.AsNoTracking().Where(a => a.TeamId == teamId), query, cancellationToken);
    }

    public async Task<PagedResult<AuditEntryDto>> ListForFlightAsync(Guid flightId, AuditQuery query, CancellationToken cancellationToken)
    {
        await validator.EnsureValidAsync(query, cancellationToken);
        var (teamId, _) = await FlightAccess.EnsureAsync(db, access, flightId, TeamRole.Viewer, cancellationToken);

        // The team id is part of the filter so that an entry can never leak to another team, even if ids collided.
        return await PageAsync(db.AuditEntries.AsNoTracking().Where(a => a.FlightId == flightId && a.TeamId == teamId), query with { FlightId = null }, cancellationToken);
    }

    public async Task<PagedResult<AuditEntryDto>> ListAllAsync(AuditQuery query, CancellationToken cancellationToken)
    {
        await validator.EnsureValidAsync(query, cancellationToken);
        return await PageAsync(db.AuditEntries.AsNoTracking(), query, cancellationToken);
    }

    internal static async Task<IReadOnlyList<AuditEntryDto>> ToDtosAsync(IUserDirectory users, IReadOnlyList<AuditEntry> entries, CancellationToken cancellationToken)
    {
        var name = await UserNames.ResolveAsync(users, entries.Select(e => e.ActorId), cancellationToken);
        return [.. entries.Select(e => new AuditEntryDto(e.Id, e.Action, e.EntityType, e.EntityId, e.FlightId, name(e.ActorId), e.Summary, e.Data, e.CorrelationId, e.CreatedAt))];
    }

    private async Task<PagedResult<AuditEntryDto>> PageAsync(IQueryable<AuditEntry> entries, AuditQuery query, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(query.Action))
        {
            var action = query.Action.Trim();
            entries = action.EndsWith('.') ? entries.Where(a => a.Action.StartsWith(action)) : entries.Where(a => a.Action == action);
        }

        if (!string.IsNullOrWhiteSpace(query.EntityType))
        {
            entries = entries.Where(a => a.EntityType == query.EntityType);
        }

        if (query.EntityId is { } entityId)
        {
            entries = entries.Where(a => a.EntityId == entityId);
        }

        if (query.FlightId is { } flightId)
        {
            entries = entries.Where(a => a.FlightId == flightId);
        }

        if (query.ActorId is { } actorId)
        {
            entries = entries.Where(a => a.ActorId == actorId);
        }

        if (query.From is { } from)
        {
            entries = entries.Where(a => a.CreatedAt >= from);
        }

        if (query.To is { } to)
        {
            entries = entries.Where(a => a.CreatedAt <= to);
        }

        var paging = new PageRequest(query.Page, query.PageSize);
        var total = await entries.CountAsync(cancellationToken);
        var page = await entries.OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id)
            .Skip(paging.Skip).Take(paging.SafePageSize).ToListAsync(cancellationToken);
        return new PagedResult<AuditEntryDto>(await ToDtosAsync(users, page, cancellationToken), paging.SafePage, paging.SafePageSize, total);
    }
}

internal sealed class AuditQueryValidator : AbstractValidator<AuditQuery>
{
    public AuditQueryValidator()
    {
        RuleFor(x => x.Action).MaximumLength(AuditEntry.ActionMaxLength);
        RuleFor(x => x.EntityType).MaximumLength(AuditEntry.EntityTypeMaxLength);
        RuleFor(x => x.To).GreaterThanOrEqualTo(x => x.From).When(x => x.From is not null && x.To is not null)
            .WithMessage("'to' must not be earlier than 'from'.");
        RuleFor(x => x.PageSize).InclusiveBetween(1, AuditQuery.MaxPageSize);
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
    }
}
