using FlightLog.Domain.Analysis;
using FlightLog.Domain.Common;
using FlightLog.Domain.FlightData;
using static FlightLog.Application.Analysis.RuleHelpers;

namespace FlightLog.Application.Analysis.Rules;

/// <summary>Time in flight for which core signals have no samples, and logger dropouts.</summary>
internal sealed class DataGapRule : IAnalysisRule
{
    private const int RateMultiple = 10;
    private static readonly TimeSpan MinGap = TimeSpan.FromMilliseconds(500);

    public RuleDescriptor Descriptor { get; } = new(
        "data.gaps", 1, "data", "Unexpected data gaps",
        "Reports gaps while armed in which a core signal has no samples for longer than 10 times its usual sample interval (at least 0.5 s), and "
        + "dropouts the logger itself recorded. Gaps of several signals less than 1 s apart are reported as one finding listing all affected "
        + "signals; a gap of 2 s or longer is a warning, shorter ones are information.",
        ["Normalized: attitude.roll, gps.fix_type, battery.voltage", "PX4: sensor_combined.gyro_rad[0]", "ArduPilot: IMU.GyrX", "Events: logger dropouts"],
        "Applies to the core signals the log has; not applicable when it has none of them and no dropout records.");

    public async Task<RuleEvaluation> EvaluateAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        var candidates = new List<string> { "attitude.roll", "gps.fix_type", "battery.voltage" };
        candidates.Add(context.Autopilot == Autopilot.ArduPilot ? "IMU.GyrX" : "sensor_combined.gyro_rad[0]");
        var gaps = new List<(TimeRange Range, string Signal, long ExpectedUs)>();
        var used = new List<string>();
        foreach (var name in candidates.Where(context.HasSignal))
        {
            var series = await context.SignalAsync(name, cancellationToken);
            if (series is null || SeriesMath.MedianIntervalUs(series.Timestamps) is not { } median)
            {
                continue;
            }

            used.Add(name);
            var minGapUs = Math.Max(median * RateMultiple, (long)(MinGap.TotalMilliseconds * 1000));
            gaps.AddRange(SeriesMath.FindGaps(series.Timestamps, context.Windows, minGapUs).Select(g => (g, name, median)));
        }

        var dropouts = context.Events.Where(e => e.Kind == FlightEventKind.Dropout && InWindows(context.Windows, e.TimestampUs)).ToList();
        if (used.Count == 0 && !context.Events.Any(e => e.Kind == FlightEventKind.Dropout))
        {
            return RuleEvaluation.NotApplicable("The log has none of the core signals this rule checks and no dropout records.");
        }

        foreach (var d in dropouts)
        {
            var ms = double.TryParse(d.Data?.GetValueOrDefault("durationMs"), System.Globalization.NumberStyles.Float, Invariant, out var value) ? value : 0;
            gaps.Add((new TimeRange(d.TimestampUs, d.TimestampUs + (long)(ms * 1000)), $"event:{d.Source}", 0));
        }

        var merged = SeriesMath.Merge(gaps.Select(g => new ConditionInterval(g.Range.StartUs, g.Range.EndUs, 1, 0, 0)), TimeSpan.FromSeconds(1));
        var findings = merged.Select(m =>
        {
            var parts = gaps.Where(g => g.Range.Overlaps(m.Range)).ToList();
            var pieces = SeriesMath.Merge(parts.Select(p => new ConditionInterval(p.Range.StartUs, p.Range.EndUs, 1, 0, 0)), TimeSpan.Zero);
            var noData = pieces.Sum(p => p.DurationSeconds);
            var longest = pieces.Max(p => p.DurationSeconds);
            var signals = parts.Select(p => p.Signal).Distinct().ToList();
            var measured = new Dictionary<string, double> { ["durationS"] = Math.Round(noData, 3), ["longestGapS"] = Math.Round(longest, 3) };
            foreach (var p in parts.Where(p => p.ExpectedUs > 0).GroupBy(p => p.Signal))
            {
                measured[$"usualIntervalMs:{p.Key}"] = Math.Round(p.First().ExpectedUs / 1000.0, 1);
            }

            var loggerDropout = signals.Any(s => s.StartsWith("event:", StringComparison.Ordinal));
            return new FindingDraft
            {
                Title = loggerDropout ? "Logger dropout" : "Data gap",
                Description = $"No data for {Seconds(noData)} in {string.Join(", ", signals)}"
                    + (loggerDropout ? "; the logger recorded a dropout (it could not write all data)." : "."),
                Severity = longest >= 2 ? FindingSeverity.Warning : FindingSeverity.Info,
                Confidence = loggerDropout ? FindingConfidence.High : FindingConfidence.Medium,
                StartUs = m.StartUs,
                EndUs = m.EndUs,
                Signals = signals,
                Measured = measured,
                Threshold = new Dictionary<string, double> { ["usualIntervalMultiple"] = RateMultiple, ["minGapS"] = MinGap.TotalSeconds, ["warningS"] = 2 },
                ThresholdSource = "Heuristic of this rule (10× the signal's median sample interval, at least 0.5 s; 2 s or longer is a warning).",
                OccurrenceCount = parts.Count,
                Limitations = Limitations(
                    "A gap means nothing was logged; the log alone cannot tell whether the sensor stopped or only the logger missed data.",
                    context.WindowLimitation),
            };
        }).ToList();
        return RuleEvaluation.Applied(findings, used.Count == 0 ? ["events"] : [.. used, "events"]);
    }
}
