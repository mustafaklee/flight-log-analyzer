using FlightLog.Application.Abstractions;
using FlightLog.Application.Authorization;
using FlightLog.Application.Common;
using FlightLog.Application.Analysis;
using FlightLog.Domain.Analysis;
using FlightLog.Domain.Flights;
using FlightLog.Domain.Teams;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace FlightLog.Application.Flights;

public interface IFlightService
{
    Task<PagedResult<FlightListItemDto>> ListAsync(FlightQuery query, CancellationToken cancellationToken);

    Task<FlightDto> GetAsync(Guid flightId, CancellationToken cancellationToken);

    Task<FlightDto> UpdateAsync(Guid flightId, UpdateFlightRequest request, CancellationToken cancellationToken);

    Task DeleteAsync(Guid flightId, CancellationToken cancellationToken);
}

internal sealed class FlightService(
    IApplicationDbContext db,
    ITeamAccessService access,
    IFileStorage storage,
    TimeProvider clock,
    IValidator<FlightQuery> queryValidator,
    IValidator<UpdateFlightRequest> updateValidator) : IFlightService
{
    public async Task<PagedResult<FlightListItemDto>> ListAsync(FlightQuery query, CancellationToken cancellationToken)
    {
        await queryValidator.EnsureValidAsync(query, cancellationToken);
        var paging = new PageRequest(query.Page, query.PageSize);

        var flights = db.Flights.AsNoTracking();
        if (query.TeamId is { } teamId)
        {
            await access.EnsureRoleAsync(teamId, TeamRole.Viewer, cancellationToken);
            flights = flights.Where(f => f.TeamId == teamId);
        }
        else if (await access.GetReadableTeamIdsAsync(cancellationToken) is { } readable)
        {
            flights = flights.Where(f => readable.Contains(f.TeamId));
        }

        // Filters on ids of other teams simply match nothing; they never reveal anything.
        if (query.ProjectId is { } projectId)
        {
            flights = flights.Where(f => f.ProjectId == projectId);
        }

        if (query.AircraftId is { } aircraftId)
        {
            flights = flights.Where(f => f.AircraftId == aircraftId);
        }

        if (query.Autopilot is { } autopilot)
        {
            flights = flights.Where(f => f.Autopilot == autopilot);
        }

        if (!string.IsNullOrWhiteSpace(query.Firmware))
        {
            var firmware = query.Firmware.Trim();
            flights = flights.Where(f => f.FirmwareVersion == firmware);
        }

        if (query.From is { } from)
        {
            flights = flights.Where(f => f.OccurredAt >= from);
        }

        if (query.To is { } to)
        {
            flights = flights.Where(f => f.OccurredAt <= to);
        }

        if (query.Status is { } status)
        {
            flights = flights.Where(f => db.FlightLogFiles.Any(l => l.FlightId == f.Id && l.Status == status));
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim().ToLowerInvariant();
#pragma warning disable CA1304, CA1311, CA1862 // Translated to SQL lower(); culture overloads are not translatable.
            flights = flights.Where(f => f.Name.ToLower().Contains(term)
                || db.FlightLogFiles.Any(l => l.FlightId == f.Id && l.OriginalFileName.ToLower().Contains(term)));
#pragma warning restore CA1304, CA1311, CA1862
        }

        var total = await flights.CountAsync(cancellationToken);

        flights = query.Sort switch
        {
            FlightSort.OccurredAtAsc => flights.OrderBy(f => f.OccurredAt).ThenBy(f => f.Id),
            FlightSort.NameAsc => flights.OrderBy(f => f.Name).ThenBy(f => f.Id),
            FlightSort.NameDesc => flights.OrderByDescending(f => f.Name).ThenBy(f => f.Id),
            FlightSort.DurationDesc => flights.OrderBy(f => f.DurationUs == null).ThenByDescending(f => f.DurationUs).ThenBy(f => f.Id),
            FlightSort.DurationAsc => flights.OrderBy(f => f.DurationUs == null).ThenBy(f => f.DurationUs).ThenBy(f => f.Id),
            _ => flights.OrderByDescending(f => f.OccurredAt).ThenByDescending(f => f.Id),
        };

        var rows = await flights
            .Skip(paging.Skip)
            .Take(paging.SafePageSize)
            .Select(f => new
            {
                Flight = f,
                ProjectName = db.Projects.Where(p => p.Id == f.ProjectId).Select(p => p.Name).First(),
                AircraftName = db.Aircraft.Where(a => a.Id == f.AircraftId).Select(a => a.Name).FirstOrDefault(),
                File = db.FlightLogFiles.Where(l => l.FlightId == f.Id).OrderByDescending(l => l.UploadedAt).FirstOrDefault(),
                Analysis = db.AnalysisRuns.Where(r => r.FlightId == f.Id && r.Status == AnalysisRunStatus.Succeeded)
                    .OrderByDescending(r => r.FinishedAt)
                    .Select(r => new { r.Id, r.FinishedAt, r.FindingCount, r.InfoCount, r.WarningCount, r.CriticalCount })
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        var items = rows.Select(r => new FlightListItemDto(
                r.Flight.Id, r.Flight.TeamId, r.Flight.ProjectId, r.ProjectName, r.Flight.AircraftId, r.AircraftName,
                r.Flight.Name, r.Flight.Autopilot, r.Flight.FirmwareVersion, r.Flight.StartedAt, r.Flight.OccurredAt,
                FlightMapping.ToSeconds(r.Flight.DurationUs), r.File?.Status, r.File?.Id, r.File?.OriginalFileName,
                r.File?.SizeBytes, r.Flight.CreatedAt,
                r.Analysis is null ? null : new AnalysisSummaryDto(r.Analysis.Id, r.Analysis.FinishedAt, r.Analysis.FindingCount, r.Analysis.InfoCount, r.Analysis.WarningCount, r.Analysis.CriticalCount)))
            .ToList();

        return new PagedResult<FlightListItemDto>(items, paging.SafePage, paging.SafePageSize, total);
    }

    public async Task<FlightDto> GetAsync(Guid flightId, CancellationToken cancellationToken)
    {
        var flight = await LoadAuthorizedAsync(flightId, TeamRole.Viewer, cancellationToken);
        return await ToDtoAsync(flight, cancellationToken);
    }

    public async Task<FlightDto> UpdateAsync(Guid flightId, UpdateFlightRequest request, CancellationToken cancellationToken)
    {
        await updateValidator.EnsureValidAsync(request, cancellationToken);
        var flight = await LoadAuthorizedAsync(flightId, TeamRole.Engineer, cancellationToken);
        if (flight.Version != request.Version)
        {
            throw StaleVersion();
        }

        if (request.AircraftId is { } aircraftId && aircraftId != Guid.Empty)
        {
            await EnsureAircraftInTeamAsync(db, flight.TeamId, aircraftId, cancellationToken);
        }

        flight.Update(request.Name, request.Notes, request.AircraftId, clock.GetUtcNow());
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw StaleVersion();
        }

        return await ToDtoAsync(flight, cancellationToken);
    }

    public async Task DeleteAsync(Guid flightId, CancellationToken cancellationToken)
    {
        var flight = await LoadAuthorizedAsync(flightId, TeamRole.ProjectManager, cancellationToken);
        var files = await db.FlightLogFiles.Where(l => l.FlightId == flightId).ToListAsync(cancellationToken);

        db.FlightLogFiles.RemoveRange(files);
        db.Flights.Remove(flight);
        await db.SaveChangesAsync(cancellationToken);

        // Derived data (signal chunks) belongs to this flight only; the catalog rows went with the flight.
        await storage.DeletePrefixAsync($"derived/{flightId:N}/", cancellationToken);

        // Raw objects are content addressed and may be shared by uploads of other teams; delete only orphans.
        foreach (var key in files.Select(f => f.StorageKey).Distinct())
        {
            if (!await db.FlightLogFiles.AnyAsync(l => l.StorageKey == key, cancellationToken))
            {
                await storage.DeleteAsync(key, cancellationToken);
            }
        }
    }

    internal static async Task EnsureAircraftInTeamAsync(IApplicationDbContext db, Guid teamId, Guid aircraftId, CancellationToken cancellationToken)
    {
        if (!await db.Aircraft.AnyAsync(a => a.Id == aircraftId && a.TeamId == teamId, cancellationToken))
        {
            throw new NotFoundException("Aircraft", aircraftId);
        }
    }

    private async Task<Flight> LoadAuthorizedAsync(Guid flightId, TeamRole required, CancellationToken cancellationToken)
    {
        var flight = await db.Flights.SingleOrDefaultAsync(f => f.Id == flightId, cancellationToken)
            ?? throw new NotFoundException("Flight", flightId);
        await access.EnsureResourceRoleAsync(flight.TeamId, required, "Flight", flightId, cancellationToken);
        return flight;
    }

    private async Task<FlightDto> ToDtoAsync(Flight flight, CancellationToken cancellationToken)
    {
        var projectName = await db.Projects.Where(p => p.Id == flight.ProjectId).Select(p => p.Name).SingleAsync(cancellationToken);
        var aircraftName = flight.AircraftId is null
            ? null
            : await db.Aircraft.Where(a => a.Id == flight.AircraftId).Select(a => a.Name).SingleOrDefaultAsync(cancellationToken);
        var file = await db.FlightLogFiles.AsNoTracking()
            .Where(l => l.FlightId == flight.Id)
            .OrderByDescending(l => l.UploadedAt)
            .FirstOrDefaultAsync(cancellationToken);
        var analysis = await db.AnalysisRuns.AsNoTracking()
            .Where(r => r.FlightId == flight.Id && r.Status == AnalysisRunStatus.Succeeded)
            .OrderByDescending(r => r.FinishedAt)
            .Select(r => new AnalysisSummaryDto(r.Id, r.FinishedAt, r.FindingCount, r.InfoCount, r.WarningCount, r.CriticalCount))
            .FirstOrDefaultAsync(cancellationToken);
        return FlightMapping.ToDto(flight, projectName, aircraftName, file, analysis);
    }

    private static ConflictException StaleVersion() =>
        new("flight.version_mismatch", "The flight was modified by someone else. Reload it and try again.");
}
