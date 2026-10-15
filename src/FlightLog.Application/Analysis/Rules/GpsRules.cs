using FlightLog.Domain.Analysis;
using FlightLog.Domain.Common;
using static FlightLog.Application.Analysis.RuleHelpers;

namespace FlightLog.Application.Analysis.Rules;

/// <summary>The receiver lost its 3D fix after having had one.</summary>
internal sealed class GpsFixLossRule : IAnalysisRule
{
    private const string FixType = "gps.fix_type";
    private static readonly TimeSpan MinDuration = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxSampleGap = TimeSpan.FromSeconds(2);

    public RuleDescriptor Descriptor { get; } = new(
        "gps.fix_loss", 1, "gps", "GPS 3D fix lost",
        "Reports periods of at least 1 s in which the GPS fix type the receiver reports drops below 3 (no 3D fix) after a 3D fix had been reached. 10 s or longer is critical.",
        [FixType],
        "Needs the normalized GPS fix type. Not applicable when the log has no GPS or the receiver never reached a 3D fix (for example indoor flights). Only armed periods are analyzed when the log records arming.");

    public async Task<RuleEvaluation> EvaluateAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        var fix = await context.SignalAsync(FixType, cancellationToken);
        if (fix is null)
        {
            return RuleEvaluation.NotApplicable("The log has no GPS fix type (no GPS, or a GPS message this normalization version does not map).");
        }

        var first3d = Array.FindIndex(fix.Values, v => v >= 3);
        if (first3d < 0)
        {
            return RuleEvaluation.NotApplicable("The GPS never reached a 3D fix in this log, so a loss of fix cannot be judged.");
        }

        var windows = From(context.Windows, fix.Timestamps[first3d]);
        var coverage = SeriesMath.Coverage(fix, windows, MaxSampleGap);
        var findings = SeriesMath.FindIntervals(fix, v => v < 3, windows, MinDuration, TimeSpan.FromSeconds(2), MaxSampleGap)
            .Select(i => new FindingDraft
            {
                Title = "GPS 3D fix lost",
                Description = $"The GPS reported no 3D fix for {Seconds(i.DurationSeconds)} (lowest fix type {Number(i.Min, "0")}).",
                Severity = i.DurationSeconds >= 10 ? FindingSeverity.Critical : FindingSeverity.Warning,
                Confidence = MeasuredConfidence(coverage, documentedThreshold: true),
                StartUs = i.StartUs,
                EndUs = i.EndUs,
                Signals = [FixType],
                Measured = new Dictionary<string, double> { ["durationS"] = Math.Round(i.DurationSeconds, 2), ["minFixType"] = i.Min },
                Threshold = new Dictionary<string, double> { ["fixType"] = 3, ["minDurationS"] = MinDuration.TotalSeconds, ["criticalDurationS"] = 10 },
                ThresholdSource = "Fix type scale of the autopilot (3 = 3D fix); the 1 s and 10 s durations are heuristics of this rule.",
                DataCoverage = coverage,
                Limitations = Limitations(
                    "The fix type is the receiver's own report and says nothing about position accuracy.",
                    context.WindowLimitation),
            }).ToList();
        return RuleEvaluation.Applied(findings, [FixType]);
    }
}

/// <summary>Few satellites or a high HDOP while the receiver has a 3D fix.</summary>
internal sealed class GpsQualityRule : IAnalysisRule
{
    private const string FixType = "gps.fix_type";
    private const string Satellites = "gps.satellites";
    private const string Hdop = "gps.hdop";
    private const double DefaultMinSatellites = 6;
    private const double DefaultMaxHdop = 2.0;
    private static readonly TimeSpan MinDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxSampleGap = TimeSpan.FromSeconds(2);

    public RuleDescriptor Descriptor { get; } = new(
        "gps.quality", 1, "gps", "GPS quality degraded",
        "Reports periods of at least 5 s with a 3D fix in which the number of satellites is below the minimum or the HDOP above the maximum. "
        + "The limits come from the vehicle's parameters where the autopilot has one (ArduPilot AHRS_GPS_MINSATS and GPS_HDOP_GOOD, PX4 EKF2_REQ_NSATS); "
        + "otherwise 6 satellites and HDOP 2.0 are used as heuristics.",
        [FixType, Satellites, Hdop],
        "Needs the normalized GPS fix type and satellites and/or HDOP. Not applicable without them or without any 3D fix. Only armed periods are analyzed when the log records arming.");

    public async Task<RuleEvaluation> EvaluateAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        var fix = await context.SignalAsync(FixType, cancellationToken);
        var sats = await context.SignalAsync(Satellites, cancellationToken);
        var hdop = await context.SignalAsync(Hdop, cancellationToken);
        if (fix is null || (sats is null && hdop is null))
        {
            return RuleEvaluation.NotApplicable("The log has no GPS fix type together with satellite count or HDOP.");
        }

        var fixed3d = SeriesMath.FindIntervals(fix, v => v >= 3, context.Windows, TimeSpan.Zero, TimeSpan.Zero, MaxSampleGap).Select(i => i.Range).ToList();
        if (fixed3d.Count == 0)
        {
            return RuleEvaluation.NotApplicable("The GPS had no 3D fix in the analyzed time, so its quality cannot be judged.");
        }

        var (minSats, satsSource) = MinSatellites(context);
        var (maxHdop, hdopSource) = MaxHdop(context);
        var findings = new List<FindingDraft>();
        var used = new List<string> { FixType };

        if (sats is not null)
        {
            used.Add(Satellites);
            var coverage = SeriesMath.Coverage(sats, fixed3d, MaxSampleGap);
            findings.AddRange(SeriesMath.FindIntervals(sats, v => v < minSats, fixed3d, MinDuration, TimeSpan.FromSeconds(5), MaxSampleGap).Select(i => new FindingDraft
            {
                Title = "Few GPS satellites",
                Description = $"Only {Number(i.Min, "0")} to {Number(i.Max, "0")} satellites were used for {Seconds(i.DurationSeconds)}; the limit is {Number(minSats, "0")}.",
                Severity = FindingSeverity.Warning,
                Confidence = MeasuredConfidence(coverage, satsSource is not null),
                StartUs = i.StartUs,
                EndUs = i.EndUs,
                Signals = [Satellites, FixType],
                Measured = new Dictionary<string, double> { ["minSatellites"] = i.Min, ["durationS"] = Math.Round(i.DurationSeconds, 2) },
                Threshold = new Dictionary<string, double> { ["minSatellites"] = minSats, ["minDurationS"] = MinDuration.TotalSeconds },
                ThresholdSource = satsSource ?? "Heuristic (6 satellites); the log has no minimum-satellite parameter.",
                DataCoverage = coverage,
                Limitations = context.WindowLimitation,
            }));
        }

        if (hdop is not null)
        {
            used.Add(Hdop);
            var coverage = SeriesMath.Coverage(hdop, fixed3d, MaxSampleGap);
            findings.AddRange(SeriesMath.FindIntervals(hdop, v => v > maxHdop, fixed3d, MinDuration, TimeSpan.FromSeconds(5), MaxSampleGap).Select(i => new FindingDraft
            {
                Title = "High GPS HDOP",
                Description = $"HDOP was between {Number(i.Min)} and {Number(i.Max)} for {Seconds(i.DurationSeconds)}; the limit is {Number(maxHdop)}.",
                Severity = FindingSeverity.Warning,
                Confidence = MeasuredConfidence(coverage, hdopSource is not null),
                StartUs = i.StartUs,
                EndUs = i.EndUs,
                Signals = [Hdop, FixType],
                Measured = new Dictionary<string, double> { ["maxHdop"] = i.Max, ["durationS"] = Math.Round(i.DurationSeconds, 2) },
                Threshold = new Dictionary<string, double> { ["maxHdop"] = maxHdop, ["minDurationS"] = MinDuration.TotalSeconds },
                ThresholdSource = hdopSource ?? "Heuristic (HDOP 2.0); the autopilot has no HDOP limit parameter in this log.",
                DataCoverage = coverage,
                Limitations = Limitations("HDOP only describes satellite geometry, not multipath or interference.", context.WindowLimitation),
            }));
        }

        return RuleEvaluation.Applied(findings, used);
    }

    private static (double Value, string? Source) MinSatellites(AnalysisContext context)
    {
        var name = context.Autopilot switch
        {
            Autopilot.ArduPilot => "AHRS_GPS_MINSATS",
            Autopilot.Px4 => "EKF2_REQ_NSATS",
            _ => null,
        };
        return name is not null && context.Parameter(name) is { } value && value > 0
            ? (value, $"Vehicle parameter {name}")
            : (DefaultMinSatellites, null);
    }

    private static (double Value, string? Source) MaxHdop(AnalysisContext context)
    {
        // GPS_HDOP_GOOD is HDOP × 100 (default 140 = 1.4); ArduPilot uses it for its pre-arm GPS check.
        return context.Autopilot == Autopilot.ArduPilot && context.Parameter("GPS_HDOP_GOOD") is { } value && value > 0
            ? (value / 100.0, "Vehicle parameter GPS_HDOP_GOOD (÷100)")
            : (DefaultMaxHdop, null);
    }
}
