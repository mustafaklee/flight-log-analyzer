using FlightLog.Application.Abstractions;
using FlightLog.Application.Analysis;
using FlightLog.Domain.Analysis;
using Microsoft.EntityFrameworkCore;

namespace FlightLog.Application.Ai;

/// <summary>Loads what <see cref="FlightContextBuilder"/> needs: the rule analysis, events, parameters and signal statistics.</summary>
internal static class FlightContextLoader
{
    /// <summary>
    /// Signals summarized over the analyzed flight time when present. GPS position is deliberately not part of
    /// the context: the report does not need where the flight took place.
    /// </summary>
    public static readonly IReadOnlyList<string> KeySignals =
    [
        "battery.voltage", "battery.current", "battery.remaining",
        "gps.fix_type", "gps.satellites", "gps.hdop", "gps.alt_msl", "gps.ground_speed",
        "attitude.roll", "attitude.pitch",
    ];

    private static readonly HashSet<string> Excluded = new(StringComparer.Ordinal) { "gps.lat", "gps.lon" };

    public static async Task<FlightContextInput> LoadAsync(
        IApplicationDbContext db, IFileStorage storage, Guid flightId, Guid analysisRunId, CancellationToken cancellationToken)
    {
        var flight = await db.Flights.AsNoTracking().SingleAsync(f => f.Id == flightId, cancellationToken);
        var run = await db.AnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == analysisRunId, cancellationToken);
        var findings = await db.AnalysisFindings.AsNoTracking().Where(f => f.RunId == analysisRunId).ToListAsync(cancellationToken);
        var rules = await db.AnalysisRuleResults.AsNoTracking().Where(r => r.RunId == analysisRunId).ToListAsync(cancellationToken);
        var (signals, log, events, parameters) = await AnalysisInputs.LoadAsync(db, storage, flight, cancellationToken);
        var analysis = new AnalysisContext(flight.Autopilot, flight.VehicleType, log, events, parameters, signals);
        var units = await db.FlightSignals.AsNoTracking().Where(s => s.FlightId == flightId).Select(s => new { s.Name, s.Unit })
            .ToDictionaryAsync(s => s.Name, s => s.Unit, StringComparer.Ordinal, cancellationToken);

        var statistics = new List<SignalStatistic>();
        foreach (var name in KeySignals.Where(analysis.HasSignal))
        {
            if (await analysis.SignalAsync(name, cancellationToken) is { } series
                && SignalStatistics.Compute(series, analysis.Windows) is { } stat)
            {
                statistics.Add(stat with { Unit = units.GetValueOrDefault(name) });
            }
        }

        foreach (var finding in findings.OrderByDescending(f => f.Severity).ThenBy(f => f.StartUs))
        {
            var window = SignalStatistics.WindowAround(finding.StartUs, finding.EndUs, log);
            foreach (var name in finding.Signals.Where(s => !s.Contains(':', StringComparison.Ordinal) && !Excluded.Contains(s) && analysis.HasSignal(s)).Distinct())
            {
                if (await analysis.SignalAsync(name, cancellationToken) is { } series
                    && SignalStatistics.Compute(series, [window]) is { } stat)
                {
                    statistics.Add(stat with { Unit = units.GetValueOrDefault(name), FindingId = finding.Id.ToString() });
                }
            }
        }

        return new FlightContextInput(
            flight.Autopilot, flight.VehicleType, flight.FirmwareVersion, flight.Hardware, log, analysis.ArmedIntervals, run.Id, run.EngineVersion,
            findings, rules, events, analysis.InitialParameters, analysis.ParameterChanges, statistics);
    }
}

/// <summary>Plain statistics over stored samples.</summary>
public static class SignalStatistics
{
    /// <summary>Min, max and mean of the finite samples inside the ranges; null when there is none.</summary>
    public static SignalStatistic? Compute(TimeSeries series, IReadOnlyList<TimeRange> ranges)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(ranges);
        if (ranges.Count == 0)
        {
            return null;
        }

        long count = 0;
        double sum = 0, min = double.PositiveInfinity, max = double.NegativeInfinity;
        long first = long.MaxValue, last = long.MinValue;
        for (var i = 0; i < series.Count; i++)
        {
            var t = series.Timestamps[i];
            var v = series.Values[i];
            if (!double.IsFinite(v) || !InAny(ranges, t))
            {
                continue;
            }

            count++;
            sum += v;
            min = Math.Min(min, v);
            max = Math.Max(max, v);
            first = Math.Min(first, t);
            last = Math.Max(last, t);
        }

        return count == 0
            ? null
            : new SignalStatistic(series.Name, series.Unit, null, ranges.Min(r => r.StartUs), ranges.Max(r => r.EndUs), count, min, max, sum / count);
    }

    /// <summary>The finding's range widened by 20 % of its length on each side (at least 2 s), inside the log.</summary>
    public static TimeRange WindowAround(long startUs, long endUs, TimeRange log)
    {
        var pad = Math.Max(2_000_000L, (endUs - startUs) / 5);
        return new TimeRange(Math.Max(log.StartUs, startUs - pad), Math.Min(Math.Max(log.EndUs, endUs), endUs + pad));
    }

    private static bool InAny(IReadOnlyList<TimeRange> ranges, long t)
    {
        foreach (var r in ranges)
        {
            if (r.Contains(t))
            {
                return true;
            }
        }

        return false;
    }
}
