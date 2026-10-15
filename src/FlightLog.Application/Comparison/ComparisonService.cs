using System.Text.Json;
using System.Text.RegularExpressions;
using FlightLog.Application.Abstractions;
using FlightLog.Application.Ai;
using FlightLog.Application.Analysis;
using FlightLog.Application.Authorization;
using FlightLog.Application.Common;
using FlightLog.Domain.Analysis;
using FlightLog.Domain.Common;
using FlightLog.Domain.Teams;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace FlightLog.Application.Comparison;

public interface IComparisonService
{
    Task<FlightComparisonDto> CompareAsync(CompareFlightsRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Compares flights the caller can read (any role). Computed on request from stored data and not saved: the inputs
/// are immutable per analysis, and a stored copy would only go stale when a flight is re-analyzed.
/// </summary>
internal sealed partial class ComparisonService(
    IApplicationDbContext db,
    IFileStorage storage,
    ITeamAccessService access,
    TimeProvider clock,
    IValidator<CompareFlightsRequest> validator) : IComparisonService
{
    public async Task<FlightComparisonDto> CompareAsync(CompareFlightsRequest request, CancellationToken cancellationToken)
    {
        await validator.EnsureValidAsync(request, cancellationToken);
        var ids = request.FlightIds.Distinct().ToList();

        // Authorize every flight before loading any data, so that nothing is computed for a flight the caller cannot read.
        var flights = await db.Flights.AsNoTracking().Where(f => ids.Contains(f.Id)).ToListAsync(cancellationToken);
        foreach (var id in ids)
        {
            var flight = flights.SingleOrDefault(f => f.Id == id) ?? throw new NotFoundException("Flight", id);
            await access.EnsureResourceRoleAsync(flight.TeamId, TeamRole.Viewer, "Flight", id, cancellationToken);
        }

        var inputs = new List<ComparisonInput>();
        foreach (var id in ids)
        {
            inputs.Add(await LoadAsync(flights.Single(f => f.Id == id), cancellationToken));
        }

        return FlightComparisonCalculator.Compute(inputs, clock.GetUtcNow());
    }

    private async Task<ComparisonInput> LoadAsync(Domain.Flights.Flight flight, CancellationToken cancellationToken)
    {
        var projectName = await db.Projects.Where(p => p.Id == flight.ProjectId).Select(p => p.Name).SingleAsync(cancellationToken);
        var aircraftName = flight.AircraftId is null
            ? null
            : await db.Aircraft.Where(a => a.Id == flight.AircraftId).Select(a => a.Name).SingleOrDefaultAsync(cancellationToken);
        var processed = await db.FlightSignalSeries.AnyAsync(s => s.FlightId == flight.Id, cancellationToken);
        var run = await db.AnalysisRuns.AsNoTracking().Where(r => r.FlightId == flight.Id && r.Status == AnalysisRunStatus.Succeeded)
            .OrderByDescending(r => r.FinishedAt).ThenByDescending(r => r.Id).FirstOrDefaultAsync(cancellationToken);
        var findings = run is null ? [] : await db.AnalysisFindings.AsNoTracking().Where(f => f.RunId == run.Id).ToListAsync(cancellationToken);
        var rules = run is null ? [] : await db.AnalysisRuleResults.AsNoTracking().Where(r => r.RunId == run.Id).ToListAsync(cancellationToken);
        var ai = await LatestAiReportAsync(flight.Id, cancellationToken);

        if (!processed)
        {
            return new ComparisonInput(
                flight.Id, flight.Name, projectName, flight.AircraftId, aircraftName, flight.Autopilot, flight.FirmwareVersion, flight.VehicleType,
                flight.Hardware, flight.StartedAt, flight.DurationUs / 1e6, false, [], false, [], new Dictionary<string, TimeSeries>(), [],
                run, findings, rules, ai);
        }

        var (source, log, events, parameters) = await AnalysisInputs.LoadAsync(db, storage, flight, cancellationToken);
        var context = new AnalysisContext(flight.Autopilot, flight.VehicleType, log, events, parameters, source);
        var signals = new Dictionary<string, TimeSeries>(StringComparer.Ordinal);
        foreach (var name in FlightComparisonCalculator.ComparedSignals.Where(context.HasSignal))
        {
            if (await context.SignalAsync(name, cancellationToken) is { } series)
            {
                signals[name] = series;
            }
        }

        var vibration = new List<TimeSeries>();
        foreach (var name in context.SignalNames.Where(n => IsVibrationMetric(flight.Autopilot, n)).Order(StringComparer.Ordinal))
        {
            if (await context.SignalAsync(name, cancellationToken) is { } series)
            {
                vibration.Add(series);
            }
        }

        return new ComparisonInput(
            flight.Id, flight.Name, projectName, flight.AircraftId, aircraftName, flight.Autopilot, flight.FirmwareVersion, flight.VehicleType,
            flight.Hardware, flight.StartedAt, flight.DurationUs / 1e6, true, context.Windows, context.HasArmingInformation, context.Events,
            signals, vibration, run, findings, rules, ai);
    }

    private async Task<AiReportRefDto?> LatestAiReportAsync(Guid flightId, CancellationToken cancellationToken)
    {
        var latest = await db.AiAnalyses.AsNoTracking().Where(a => a.FlightId == flightId && a.Status == AiAnalysisStatus.Succeeded)
            .OrderByDescending(a => a.FinishedAt).ThenByDescending(a => a.Id)
            .Select(a => new { a.Id, a.FinishedAt, a.Model, a.Report })
            .FirstOrDefaultAsync(cancellationToken);
        if (latest is null)
        {
            return null;
        }

        var report = latest.Report is null ? null : JsonSerializer.Deserialize<AiReportDto>(latest.Report, FlightReportValidator.StorageOptions);
        return new AiReportRefDto(latest.Id, latest.FinishedAt, latest.Model, report?.OverallAssessment, report?.Summary);
    }

    internal static bool IsVibrationMetric(Autopilot autopilot, string name) => autopilot switch
    {
        Autopilot.ArduPilot => ArduPilotVibration().IsMatch(name),
        Autopilot.Px4 => Px4Vibration().IsMatch(name),
        _ => false,
    };

    [GeneratedRegex(@"^VIBE(#\d+)?\.Vibe[XYZ]$", RegexOptions.CultureInvariant)]
    private static partial Regex ArduPilotVibration();

    [GeneratedRegex(@"^vehicle_imu_status(#\d+)?\.accel_vibration_metric$", RegexOptions.CultureInvariant)]
    private static partial Regex Px4Vibration();
}

internal sealed class CompareFlightsRequestValidator : AbstractValidator<CompareFlightsRequest>
{
    public CompareFlightsRequestValidator()
    {
        RuleFor(x => x.FlightIds).NotNull();
        RuleFor(x => x.FlightIds)
            .Must(ids => ids is not null && ids.Distinct().Count() is >= CompareFlightsRequest.MinFlights and <= CompareFlightsRequest.MaxFlights)
            .WithMessage($"Compare {CompareFlightsRequest.MinFlights} to {CompareFlightsRequest.MaxFlights} different flights.");
        RuleForEach(x => x.FlightIds).NotEmpty();
    }
}
