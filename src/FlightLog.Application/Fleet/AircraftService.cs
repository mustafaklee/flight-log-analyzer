using FlightLog.Application.Abstractions;
using FlightLog.Application.Collaboration;
using FlightLog.Application.Authorization;
using FlightLog.Application.Common;
using FlightLog.Domain.Fleet;
using FlightLog.Domain.Teams;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace FlightLog.Application.Fleet;

public interface IAircraftService
{
    Task<PagedResult<AircraftDto>> ListAsync(Guid teamId, AircraftQuery query, CancellationToken cancellationToken);

    Task<AircraftDto> GetAsync(Guid aircraftId, CancellationToken cancellationToken);

    Task<AircraftDto> CreateAsync(Guid teamId, SaveAircraftRequest request, CancellationToken cancellationToken);

    Task<AircraftDto> UpdateAsync(Guid aircraftId, SaveAircraftRequest request, CancellationToken cancellationToken);

    Task DeleteAsync(Guid aircraftId, CancellationToken cancellationToken);
}

internal sealed class AircraftService(
    IApplicationDbContext db,
    ITeamAccessService access,
    IAuditTrail audit,
    TimeProvider clock,
    IValidator<SaveAircraftRequest> validator) : IAircraftService
{
    public async Task<PagedResult<AircraftDto>> ListAsync(Guid teamId, AircraftQuery query, CancellationToken cancellationToken)
    {
        await access.EnsureRoleAsync(teamId, TeamRole.Viewer, cancellationToken);

        var paging = new PageRequest(query.Page, query.PageSize);
        var aircraft = db.Aircraft.AsNoTracking().Where(a => a.TeamId == teamId);
        if (query.Autopilot is { } autopilot)
        {
            aircraft = aircraft.Where(a => a.Autopilot == autopilot);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim().ToLowerInvariant();
#pragma warning disable CA1304, CA1311, CA1862 // Translated to SQL lower(); culture overloads are not translatable.
            aircraft = aircraft.Where(a => a.Name.ToLower().Contains(term) || (a.Identifier != null && a.Identifier.ToLower().Contains(term)));
#pragma warning restore CA1304, CA1311, CA1862
        }

        var total = await aircraft.CountAsync(cancellationToken);
        var items = await aircraft
            .OrderBy(a => a.Name)
            .ThenBy(a => a.Id)
            .Skip(paging.Skip)
            .Take(paging.SafePageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<AircraftDto>(items.Select(ToDto).ToList(), paging.SafePage, paging.SafePageSize, total);
    }

    public async Task<AircraftDto> GetAsync(Guid aircraftId, CancellationToken cancellationToken) =>
        ToDto(await LoadAuthorizedAsync(aircraftId, TeamRole.Viewer, cancellationToken));

    public async Task<AircraftDto> CreateAsync(Guid teamId, SaveAircraftRequest request, CancellationToken cancellationToken)
    {
        await validator.EnsureValidAsync(request, cancellationToken);
        await access.EnsureRoleAsync(teamId, TeamRole.Engineer, cancellationToken);

        var aircraft = Aircraft.Create(teamId, request.ToDetails(), clock.GetUtcNow());
        await EnsureIdentifierIsFreeAsync(aircraft, cancellationToken);

        db.Aircraft.Add(aircraft);
        audit.Record(teamId, AuditActions.AircraftCreated, AuditEntities.Aircraft, aircraft.Id, null, aircraft.Name);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(aircraft);
    }

    public async Task<AircraftDto> UpdateAsync(Guid aircraftId, SaveAircraftRequest request, CancellationToken cancellationToken)
    {
        await validator.EnsureValidAsync(request, cancellationToken);
        var aircraft = await LoadAuthorizedAsync(aircraftId, TeamRole.Engineer, cancellationToken);
        if (request.Version is null || request.Version != aircraft.Version)
        {
            throw StaleVersion();
        }

        aircraft.Update(request.ToDetails(), clock.GetUtcNow());
        await EnsureIdentifierIsFreeAsync(aircraft, cancellationToken);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw StaleVersion();
        }

        return ToDto(aircraft);
    }

    public async Task DeleteAsync(Guid aircraftId, CancellationToken cancellationToken)
    {
        // Flights of a deleted aircraft are kept; their aircraft link is cleared by the database (ON DELETE SET NULL).
        var aircraft = await LoadAuthorizedAsync(aircraftId, TeamRole.ProjectManager, cancellationToken);
        db.Aircraft.Remove(aircraft);
        audit.Record(aircraft.TeamId, AuditActions.AircraftDeleted, AuditEntities.Aircraft, aircraft.Id, null, aircraft.Name);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<Aircraft> LoadAuthorizedAsync(Guid aircraftId, TeamRole required, CancellationToken cancellationToken)
    {
        var aircraft = await db.Aircraft.SingleOrDefaultAsync(a => a.Id == aircraftId, cancellationToken)
            ?? throw new NotFoundException("Aircraft", aircraftId);
        await access.EnsureResourceRoleAsync(aircraft.TeamId, required, "Aircraft", aircraftId, cancellationToken);
        return aircraft;
    }

    private async Task EnsureIdentifierIsFreeAsync(Aircraft aircraft, CancellationToken cancellationToken)
    {
        if (aircraft.Identifier is null)
        {
            return;
        }

        var taken = await db.Aircraft.AnyAsync(
            a => a.TeamId == aircraft.TeamId && a.Identifier == aircraft.Identifier && a.Id != aircraft.Id,
            cancellationToken);
        if (taken)
        {
            throw new ConflictException("aircraft.identifier_taken", "Another aircraft of the team already uses this identifier.");
        }
    }

    private static ConflictException StaleVersion() =>
        new("aircraft.version_mismatch", "The aircraft was modified by someone else. Reload it and try again.");

    internal static AircraftDto ToDto(Aircraft a) =>
        new(a.Id, a.TeamId, a.Name, a.Identifier, a.Type, a.Autopilot, a.FirmwareName, a.FirmwareVersion,
            a.FlightController, a.Hardware, a.Description, a.Tags.ToList(), a.CreatedAt, a.UpdatedAt, a.Version);
}
