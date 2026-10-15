using System.Globalization;
using FlightLog.Application.Analysis;
using FlightLog.Domain.Analysis;
using FlightLog.Domain.Common;
using FlightLog.Domain.FlightData;

namespace FlightLog.Application.Comparison;

/// <summary>What the comparison needs of one flight. Signals are the compared ones that the flight has.</summary>
public sealed record ComparisonInput(
    Guid Id,
    string Name,
    string ProjectName,
    Guid? AircraftId,
    string? AircraftName,
    Autopilot Autopilot,
    string? FirmwareVersion,
    string? VehicleType,
    string? Hardware,
    DateTimeOffset? StartedAt,
    double? DurationSeconds,
    bool Processed,
    IReadOnlyList<TimeRange> Windows,
    bool ArmingKnown,
    IReadOnlyList<FlightEvent> Events,
    IReadOnlyDictionary<string, TimeSeries> Signals,
    IReadOnlyList<TimeSeries> VibrationSeries,
    AnalysisRun? Run,
    IReadOnlyList<AnalysisFinding> Findings,
    IReadOnlyList<AnalysisRuleResult> RuleResults,
    AiReportRefDto? LatestAiReport);

/// <summary>Pure computation of a <see cref="FlightComparisonDto"/>; deterministic for the same inputs and time.</summary>
public static class FlightComparisonCalculator
{
    public const string Version = "comparison-v1";

    /// <summary>
    /// Normalized signals compared statistically. Yaw is left out: it is an angle on a circle, so its mean and
    /// percentiles are meaningless. GPS position is left out: it says where, not how, the vehicle flew.
    /// </summary>
    public static readonly IReadOnlyList<string> ComparedSignals =
    [
        "attitude.roll", "attitude.pitch",
        "gps.satellites", "gps.hdop", "gps.ground_speed", "gps.alt_msl",
        "battery.voltage", "battery.current", "battery.remaining",
    ];

    public static readonly IReadOnlyList<string> Limitations =
    [
        "Statistics cover the analyzed time of each flight: its armed periods, or the whole log when the log does not record arming.",
        "Percentiles are computed over the stored samples (linear interpolation between ranks), not weighted by time; uneven sampling biases them toward densely sampled periods.",
        "Values come from different logs, sensors and possibly firmware versions; a difference in numbers is a hint to investigate, not proof of a difference in the vehicles.",
        "Findings and rule results are those of each flight's latest successful rule analysis; flights analyzed with different rule engine versions are flagged.",
    ];

    private static readonly Dictionary<string, string> SignalNotes = new(StringComparer.Ordinal)
    {
        ["gps.alt_msl"] = "Absolute altitude depends on the take-off site; compare it only for flights from the same place.",
        ["battery.remaining"] = "Estimated by the autopilot from its battery settings; flights with different battery settings are not directly comparable.",
    };

    private static readonly FlightEventKind[] CountedEvents =
        [FlightEventKind.Failsafe, FlightEventKind.Error, FlightEventKind.ModeChange, FlightEventKind.Dropout];

    public static FlightComparisonDto Compute(IReadOnlyList<ComparisonInput> flights, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(flights);
        var notes = Notes(flights);
        var signals = ComparedSignals.Select(name => CompareSignal(name, flights)).Where(s => s is not null).Select(s => s!).ToList();
        var rules = flights.SelectMany(f => f.RuleResults.Select(r => r.RuleId)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(ruleId => new ComparedRuleDto(ruleId, [.. flights.Select(f => RuleResult(f, ruleId))]))
            .ToList();

        return new FlightComparisonDto(Version, now, [.. flights.Select(Flight)], signals, rules, notes, Limitations);
    }

    public static SignalStatsDto? Statistics(TimeSeries series, IReadOnlyList<TimeRange> windows)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(windows);
        var values = Values(series, windows);
        if (values.Count == 0)
        {
            return null;
        }

        values.Sort();
        var seconds = windows.Sum(w => w.DurationSeconds);
        return new SignalStatsDto(
            values.Count,
            seconds > 0 ? Round(values.Count / seconds) : 0,
            values[0],
            values[^1],
            Round(values.Average()),
            Percentile(values, 0.05),
            Percentile(values, 0.50),
            Percentile(values, 0.95));
    }

    /// <summary>Percentile of sorted values with linear interpolation between ranks (the common "type 7" definition).</summary>
    public static double Percentile(IReadOnlyList<double> sorted, double p)
    {
        ArgumentNullException.ThrowIfNull(sorted);
        if (sorted.Count == 0)
        {
            throw new ArgumentException("No values.", nameof(sorted));
        }

        var rank = p * (sorted.Count - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        return Round(sorted[lower] + ((sorted[upper] - sorted[lower]) * (rank - lower)));
    }

    /// <summary>Seconds per flight mode inside the windows, from mode change events; time before the first known mode is "unknown".</summary>
    public static IReadOnlyList<ModeShareDto> Modes(IReadOnlyList<FlightEvent> events, IReadOnlyList<TimeRange> windows)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(windows);
        var changes = events.Where(e => e.Kind == FlightEventKind.ModeChange)
            .OrderBy(e => e.TimestampUs)
            .Select(e => (At: e.TimestampUs, Mode: e.Data?.GetValueOrDefault("mode") ?? e.Text))
            .ToList();
        if (changes.Count == 0)
        {
            return [];
        }

        var seconds = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var window in windows)
        {
            var mode = changes.LastOrDefault(c => c.At <= window.StartUs).Mode ?? "unknown";
            var from = window.StartUs;
            foreach (var change in changes.Where(c => c.At > window.StartUs && c.At < window.EndUs))
            {
                Add(mode, change.At - from);
                mode = change.Mode;
                from = change.At;
            }

            Add(mode, window.EndUs - from);
        }

        var total = seconds.Values.Sum();
        return total <= 0
            ? []
            : [.. seconds.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => new ModeShareDto(p.Key, Round(p.Value), Round(p.Value / total)))];

        void Add(string mode, long us)
        {
            if (us > 0)
            {
                seconds[mode] = seconds.GetValueOrDefault(mode) + (us / 1e6);
            }
        }
    }

    private static ComparedFlightDto Flight(ComparisonInput f)
    {
        var analyzed = f.Processed && f.Windows.Count > 0 ? Round(f.Windows.Sum(w => w.DurationSeconds)) : (double?)null;
        var hasRun = f.Run is not null;
        return new ComparedFlightDto(
            f.Id, f.Name, f.ProjectName, f.AircraftName, f.Autopilot, f.FirmwareVersion, f.VehicleType, f.Hardware, f.StartedAt,
            f.DurationSeconds is { } d ? Round(d) : null, analyzed, f.ArmingKnown,
            f.Run?.Id, f.Run?.EngineVersion,
            hasRun ? f.Run!.InfoCount : null, hasRun ? f.Run!.WarningCount : null, hasRun ? f.Run!.CriticalCount : null,
            f.Findings.GroupBy(x => x.Category, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            CountedEvents.ToDictionary(
                k => k.ToString(),
                k => f.Events.Count(e => e.Kind == k && f.Windows.Any(w => w.Contains(e.TimestampUs))),
                StringComparer.Ordinal),
            f.Processed ? Modes(f.Events, f.Windows) : [],
            Battery(f),
            Vibration(f),
            f.LatestAiReport);
    }

    private static BatterySummaryDto? Battery(ComparisonInput f)
    {
        var voltage = f.Signals.TryGetValue("battery.voltage", out var v) ? Values(v, f.Windows) : [];
        var current = f.Signals.TryGetValue("battery.current", out var c) ? Values(c, f.Windows) : [];
        var remaining = f.Signals.TryGetValue("battery.remaining", out var r) ? Values(r, f.Windows) : [];
        if (voltage.Count == 0 && current.Count == 0 && remaining.Count == 0)
        {
            return null;
        }

        double? startRemaining = remaining.Count > 0 ? Round(remaining[0]) : null;
        double? endRemaining = remaining.Count > 0 ? Round(remaining[^1]) : null;
        return new BatterySummaryDto(
            voltage.Count > 0 ? Round(voltage[0]) : null,
            voltage.Count > 0 ? Round(voltage[^1]) : null,
            voltage.Count > 0 ? Round(voltage.Min()) : null,
            current.Count > 0 ? Round(current.Average()) : null,
            current.Count > 0 ? Round(current.Max()) : null,
            startRemaining,
            endRemaining,
            startRemaining - endRemaining is { } used ? Round(used) : null);
    }

    private static VibrationSummaryDto? Vibration(ComparisonInput f)
    {
        var values = f.VibrationSeries.SelectMany(s => Values(s, f.Windows)).ToList();
        if (values.Count == 0)
        {
            return null;
        }

        values.Sort();
        var metric = f.Autopilot == Autopilot.ArduPilot ? "VIBE VibeX/VibeY/VibeZ (all IMUs)" : "vehicle_imu_status.accel_vibration_metric (all IMUs)";
        return new VibrationSummaryDto(metric, f.VibrationSeries[0].Unit, f.Autopilot, Round(values.Average()), Percentile(values, 0.95), Round(values[^1]));
    }

    private static ComparedSignalDto? CompareSignal(string name, IReadOnlyList<ComparisonInput> flights)
    {
        var values = flights.Select(f => f.Signals.TryGetValue(name, out var s) ? Statistics(s, f.Windows) : null).ToList();
        if (values.All(v => v is null))
        {
            return null;
        }

        var units = flights.Select(f => f.Signals.TryGetValue(name, out var s) ? s.Unit : null).Where(u => u is not null).Distinct(StringComparer.Ordinal).ToList();
        var notes = new List<string>();
        var comparable = units.Count <= 1;
        if (!comparable)
        {
            notes.Add($"Units differ ({string.Join(", ", units)}).");
        }

        var missing = values.Count(v => v is null);
        if (missing > 0)
        {
            notes.Add($"No valid samples in {missing} of {values.Count} flights.");
        }

        var rates = values.Where(v => v is not null && v.RateHz > 0).Select(v => v!.RateHz).ToList();
        if (rates.Count > 1 && rates.Max() / rates.Min() > 2)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture,
                $"Sampling rates differ ({rates.Min():0.#} to {rates.Max():0.#} Hz); short extremes may be missing from the slower logs."));
        }

        if (SignalNotes.TryGetValue(name, out var note))
        {
            notes.Add(note);
        }

        return new ComparedSignalDto(name, units.FirstOrDefault(), comparable, notes.Count == 0 ? null : string.Join(" ", notes), values);
    }

    private static ComparedRuleResultDto? RuleResult(ComparisonInput f, string ruleId)
    {
        var result = f.RuleResults.FirstOrDefault(r => r.RuleId == ruleId);
        if (result is null)
        {
            return null;
        }

        var findings = f.Findings.Where(x => x.RuleId == ruleId).ToList();
        return new ComparedRuleResultDto(result.Status, findings.Count, findings.Count == 0 ? null : findings.Max(x => x.Severity));
    }

    private static List<ComparabilityNoteDto> Notes(IReadOnlyList<ComparisonInput> flights)
    {
        var notes = new List<ComparabilityNoteDto>();
        if (flights.Select(f => f.Autopilot).Distinct().Count() > 1)
        {
            notes.Add(new("different_autopilot",
                "The flights use different autopilots. Normalized signals (attitude, GPS, battery) are comparable; vibration metrics and rules that apply to one autopilot only are not.",
                []));
        }

        var firmware = flights.Where(f => f.FirmwareVersion is not null).Select(f => f.FirmwareVersion).Distinct(StringComparer.Ordinal).Count();
        if (firmware > 1)
        {
            notes.Add(new("different_firmware", "The flights ran different firmware versions; logging, estimators and parameter meanings can differ.", []));
        }

        if (flights.Where(f => f.VehicleType is not null).Select(f => f.VehicleType).Distinct(StringComparer.Ordinal).Count() > 1)
        {
            notes.Add(new("different_vehicle_type", "The flights are of different vehicle types; flight behaviour differs by design.", []));
        }

        if (flights.Select(f => f.AircraftId).Distinct().Count() > 1 || flights.Any(f => f.AircraftId is null))
        {
            notes.Add(new("different_or_unknown_aircraft",
                "The flights are not all linked to the same aircraft; configuration (battery, frame, sensors) may differ.",
                [.. flights.Where(f => f.AircraftId is null).Select(f => f.Id)]));
        }

        AddFor("not_processed", "The log has not been processed; no flight data can be compared.", f => !f.Processed);
        AddFor("no_arming_information", "The log does not record arming; the whole log, including time on the ground, was used.", f => f.Processed && !f.ArmingKnown);
        AddFor("no_rule_analysis", "The flight has no successful rule analysis; findings and rule results are missing.", f => f.Processed && f.Run is null);

        if (flights.Where(f => f.Run is not null).Select(f => f.Run!.EngineVersion).Distinct(StringComparer.Ordinal).Count() > 1)
        {
            notes.Add(new("different_rule_engine", "The flights were analyzed with different rule engine versions; re-analyze them to compare findings directly.", []));
        }

        var noVibration = flights.Where(f => f.Processed && f.VibrationSeries.Count == 0).Select(f => f.Id).ToList();
        if (noVibration.Count > 0)
        {
            notes.Add(new("no_vibration_metric", "The log has no vibration metric this version reads (ArduPilot VIBE or PX4 vehicle_imu_status).", noVibration));
        }

        return notes;

        void AddFor(string code, string message, Func<ComparisonInput, bool> predicate)
        {
            var ids = flights.Where(predicate).Select(f => f.Id).ToList();
            if (ids.Count > 0)
            {
                notes.Add(new(code, message, ids));
            }
        }
    }

    private static List<double> Values(TimeSeries series, IReadOnlyList<TimeRange> windows)
    {
        var values = new List<double>();
        for (var i = 0; i < series.Count; i++)
        {
            var value = series.Values[i];
            if (double.IsFinite(value) && windows.Any(w => w.Contains(series.Timestamps[i])))
            {
                values.Add(value);
            }
        }

        return values;
    }

    private static double Round(double value) => Math.Round(value, 4, MidpointRounding.AwayFromZero);
}
