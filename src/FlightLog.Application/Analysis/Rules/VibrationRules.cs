using FlightLog.Domain.Analysis;
using FlightLog.Domain.Common;
using static FlightLog.Application.Analysis.RuleHelpers;

namespace FlightLog.Application.Analysis.Rules;

/// <summary>ArduPilot vibration levels above the limits of the ArduPilot documentation.</summary>
internal sealed class VibrationLevelRule : IAnalysisRule
{
    private const double WarningLevel = 30;
    private const double CriticalLevel = 60;
    private static readonly string[] Axes = ["VibeX", "VibeY", "VibeZ"];
    private static readonly TimeSpan MinDuration = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxSampleGap = TimeSpan.FromSeconds(2);

    public RuleDescriptor Descriptor { get; } = new(
        "vibration.level", 1, "vibration", "High vibration",
        "Reports periods of at least 1 s in which an ArduPilot vibration level (VIBE VibeX/VibeY/VibeZ, m/s/s) is above 30; above 60 is critical. "
        + "These are the limits of the ArduPilot documentation (\"Measuring Vibration\"): below 30 is normally acceptable, above 60 nearly always "
        + "causes problems with position or altitude hold. All IMUs and axes are checked and overlapping periods are reported once.",
        ["ArduPilot: VIBE.VibeX, VibeY, VibeZ (every IMU instance)"],
        "Only for ArduPilot. Not applicable to PX4: it logs a different metric (vehicle_imu_status.accel_vibration_metric) for which this rule "
        + "version has no documented limit. Only armed periods are analyzed when the log records arming.");

    public async Task<RuleEvaluation> EvaluateAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        if (context.Autopilot != Autopilot.ArduPilot)
        {
            return RuleEvaluation.NotApplicable("This rule version only knows documented vibration limits for ArduPilot VIBE data.");
        }

        var names = Axes.SelectMany(f => Instances(context, "VIBE", f)).ToList();
        if (names.Count == 0)
        {
            return RuleEvaluation.NotApplicable("The log has no VIBE message.");
        }

        var intervals = new List<(ConditionInterval Interval, string Signal)>();
        var coverages = new List<double>();
        foreach (var name in names)
        {
            var series = await context.SignalAsync(name, cancellationToken);
            if (series is null)
            {
                continue;
            }

            coverages.Add(SeriesMath.Coverage(series, context.Windows, MaxSampleGap));
            intervals.AddRange(SeriesMath.FindIntervals(series, v => v > WarningLevel, context.Windows, MinDuration, TimeSpan.FromSeconds(3), MaxSampleGap)
                .Select(i => (i, name)));
        }

        var coverage = coverages.Count == 0 ? 0 : coverages.Min();
        var copter = string.Equals(context.VehicleType, "Copter", StringComparison.Ordinal);
        var findings = SeriesMath.Merge(intervals.Select(i => i.Interval), TimeSpan.FromSeconds(3)).Select(merged =>
        {
            var parts = intervals.Where(i => i.Interval.Range.Overlaps(merged.Range)).ToList();
            var measured = parts.GroupBy(p => p.Signal).ToDictionary(g => $"max:{g.Key}", g => Math.Round(g.Max(p => p.Interval.Max), 2));
            measured["durationS"] = Math.Round(merged.DurationSeconds, 2);
            return new FindingDraft
            {
                Title = merged.Max > CriticalLevel ? "Very high vibration" : "High vibration",
                Description = $"Vibration was above {Number(WarningLevel, "0")} m/s/s for {Seconds(merged.DurationSeconds)}, peaking at {Number(merged.Max, "0.0")} m/s/s "
                    + $"({string.Join(", ", parts.Select(p => p.Signal).Distinct())}).",
                Severity = merged.Max > CriticalLevel ? FindingSeverity.Critical : FindingSeverity.Warning,
                Confidence = MeasuredConfidence(coverage, documentedThreshold: true),
                StartUs = merged.StartUs,
                EndUs = merged.EndUs,
                Signals = [.. parts.Select(p => p.Signal).Distinct()],
                Measured = measured,
                Threshold = new Dictionary<string, double> { ["warning"] = WarningLevel, ["critical"] = CriticalLevel, ["minDurationS"] = MinDuration.TotalSeconds },
                ThresholdSource = "ArduPilot documentation, \"Measuring Vibration\" (30 and 60 m/s/s).",
                DataCoverage = coverage,
                OccurrenceCount = parts.Count,
                Limitations = Limitations(
                    copter ? null : "The documented limits are written for multicopters; other airframes may tolerate different levels.",
                    context.WindowLimitation),
            };
        }).ToList();
        return RuleEvaluation.Applied(findings, names);
    }
}

/// <summary>Accelerometer clipping: the sensor hit the end of its measurement range.</summary>
internal sealed class AccelerometerClippingRule : IAnalysisRule
{
    private static readonly TimeSpan MergeGap = TimeSpan.FromSeconds(5);
    private static readonly string[] ArduPilotCounters = ["Clip", "Clip0", "Clip1", "Clip2"];
    private static readonly string[] Px4Counters = ["accel_clipping[0]", "accel_clipping[1]", "accel_clipping[2]"];

    public RuleDescriptor Descriptor { get; } = new(
        "vibration.clipping", 1, "vibration", "Accelerometer clipping",
        "Reports when an accelerometer clipping counter increases: the sensor reached the end of its measurement range, so its readings were "
        + "wrong at that moment (usually severe vibration or an impact). Increases within 5 s are reported together with their total.",
        ["ArduPilot: VIBE.Clip (or Clip0/Clip1/Clip2 in older firmware)", "PX4: vehicle_imu_status.accel_clipping[0..2]"],
        "Not applicable when the log has no clipping counters. Only armed periods are analyzed when the log records arming.");

    public async Task<RuleEvaluation> EvaluateAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        var names = context.Autopilot switch
        {
            Autopilot.ArduPilot => ArduPilotCounters.SelectMany(f => Instances(context, "VIBE", f)).ToList(),
            Autopilot.Px4 => Px4Counters.SelectMany(f => Instances(context, "vehicle_imu_status", f)).ToList(),
            _ => [],
        };
        if (names.Count == 0)
        {
            return RuleEvaluation.NotApplicable("The log has no accelerometer clipping counters.");
        }

        var increases = new List<(ConditionInterval Interval, string Signal)>();
        foreach (var name in names)
        {
            var series = await context.SignalAsync(name, cancellationToken);
            if (series is null || series.Count < 2)
            {
                continue;
            }

            // Counters only grow; a step up is a clipping event. Resets (reboot of the sensor driver) are ignored.
            var deltas = new double[series.Count];
            deltas[0] = double.NaN;
            for (var i = 1; i < series.Count; i++)
            {
                var d = series.Values[i] - series.Values[i - 1];
                deltas[i] = double.IsFinite(d) && d > 0 ? d : 0;
            }

            var deltaSeries = new TimeSeries(name, null, series.Timestamps, deltas);
            foreach (var interval in SeriesMath.FindIntervals(deltaSeries, d => d > 0, context.Windows, TimeSpan.Zero, MergeGap, TimeSpan.FromDays(1)))
            {
                var total = 0d;
                for (var i = 0; i < deltaSeries.Count; i++)
                {
                    if (deltaSeries.Timestamps[i] >= interval.StartUs && deltaSeries.Timestamps[i] <= interval.EndUs && deltas[i] > 0)
                    {
                        total += deltas[i];
                    }
                }

                increases.Add((interval with { Max = total }, name));
            }
        }

        var findings = SeriesMath.Merge(increases.Select(i => i.Interval), MergeGap).Select(merged =>
        {
            var parts = increases.Where(i => i.Interval.Range.Overlaps(merged.Range)).ToList();
            var total = parts.Sum(p => p.Interval.Max);
            return new FindingDraft
            {
                Title = "Accelerometer clipping",
                Description = $"Accelerometer clipping counters grew by {Number(total, "0")} ({string.Join(", ", parts.Select(p => p.Signal).Distinct())}).",
                Severity = FindingSeverity.Warning,
                Confidence = FindingConfidence.High,
                StartUs = merged.StartUs,
                EndUs = merged.EndUs,
                Signals = [.. parts.Select(p => p.Signal).Distinct()],
                Measured = new Dictionary<string, double> { ["clippingCount"] = total },
                Threshold = new Dictionary<string, double> { ["increase"] = 0 },
                ThresholdSource = "Counter maintained by the autopilot; any increase is a clipping event.",
                OccurrenceCount = parts.Count,
                Limitations = context.WindowLimitation,
            };
        }).ToList();
        return RuleEvaluation.Applied(findings, names);
    }
}
