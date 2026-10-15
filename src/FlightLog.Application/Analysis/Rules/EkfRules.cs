using FlightLog.Domain.Analysis;
using FlightLog.Domain.Common;
using FlightLog.Domain.FlightData;
using static FlightLog.Application.Analysis.RuleHelpers;

namespace FlightLog.Application.Analysis.Rules;

/// <summary>The estimator rejected measurements: innovation test ratio above 1.</summary>
internal sealed class EkfInnovationRule : IAnalysisRule
{
    private const double Limit = 1.0;
    private static readonly TimeSpan MinDuration = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxSampleGap = TimeSpan.FromSeconds(2);

    // (measurement, PX4 estimator_status field, ArduPilot XKF4/NKF4 field)
    private static readonly (string Measurement, string Px4, string ArduPilot)[] Ratios =
    [
        ("velocity", "vel_test_ratio", "SV"),
        ("position", "pos_test_ratio", "SP"),
        ("height", "hgt_test_ratio", "SH"),
        ("magnetometer", "mag_test_ratio", "SM"),
    ];

    public RuleDescriptor Descriptor { get; } = new(
        "ekf.innovation", 1, "ekf", "EKF rejected sensor measurements",
        "Reports periods of at least 1 s in which an innovation test ratio of the estimator is above 1, which means the estimator rejected that "
        + "measurement (velocity, position, height or magnetometer) as inconsistent with its prediction. All EKF instances/cores are checked; "
        + "overlapping periods of one measurement are reported once.",
        ["PX4: estimator_status.vel_test_ratio, pos_test_ratio, hgt_test_ratio, mag_test_ratio", "ArduPilot: XKF4 (EKF3) or NKF4 (EKF2) SV, SP, SH, SM"],
        "Not applicable when the log has none of the test ratio fields (older or reduced logging). Only armed periods are analyzed when the log records arming.");

    public async Task<RuleEvaluation> EvaluateAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        var findings = new List<FindingDraft>();
        var used = new List<string>();
        foreach (var (measurement, px4, arduPilot) in Ratios)
        {
            var names = context.Autopilot switch
            {
                Autopilot.Px4 => Instances(context, "estimator_status", px4),
                Autopilot.ArduPilot => [.. Instances(context, "XKF4", arduPilot), .. Instances(context, "NKF4", arduPilot)],
                _ => [],
            };

            var intervals = new List<(ConditionInterval Interval, string Signal)>();
            var coverages = new List<double>();
            foreach (var name in names)
            {
                var series = await context.SignalAsync(name, cancellationToken);
                if (series is null)
                {
                    continue;
                }

                used.Add(name);
                coverages.Add(SeriesMath.Coverage(series, context.Windows, MaxSampleGap));
                intervals.AddRange(SeriesMath.FindIntervals(series, v => v > Limit, context.Windows, MinDuration, TimeSpan.FromSeconds(2), MaxSampleGap)
                    .Select(i => (i, name)));
            }

            if (intervals.Count == 0)
            {
                continue;
            }

            var coverage = coverages.Count == 0 ? 0 : coverages.Max();
            foreach (var merged in SeriesMath.Merge(intervals.Select(i => i.Interval), TimeSpan.FromSeconds(2)))
            {
                var signals = intervals.Where(i => i.Interval.Range.Overlaps(merged.Range)).Select(i => i.Signal).Distinct().ToList();
                findings.Add(new FindingDraft
                {
                    Title = $"EKF rejected {measurement} measurements",
                    Description = $"The {measurement} innovation test ratio was above 1 for {Seconds(merged.DurationSeconds)} (peak {Number(merged.Max)}), "
                        + $"so the estimator did not use the {measurement} measurement in that time.",
                    Severity = FindingSeverity.Warning,
                    Confidence = MeasuredConfidence(coverage, documentedThreshold: true),
                    StartUs = merged.StartUs,
                    EndUs = merged.EndUs,
                    Signals = signals,
                    Measured = new Dictionary<string, double> { ["peakTestRatio"] = merged.Max, ["durationS"] = Math.Round(merged.DurationSeconds, 2) },
                    Threshold = new Dictionary<string, double> { ["testRatio"] = Limit, ["minDurationS"] = MinDuration.TotalSeconds },
                    ThresholdSource = "Estimator definition: a test ratio above 1 means the measurement failed the innovation consistency check.",
                    DataCoverage = coverage,
                    OccurrenceCount = intervals.Count(i => i.Interval.Range.Overlaps(merged.Range)),
                    Limitations = Limitations(
                        "A rejected measurement shows that the estimator distrusted a sensor; it does not tell whether the sensor or the prediction was wrong.",
                        context.WindowLimitation),
                });
            }
        }

        return used.Count == 0
            ? RuleEvaluation.NotApplicable("The log has no EKF innovation test ratios (PX4 estimator_status or ArduPilot XKF4/NKF4).")
            : RuleEvaluation.Applied(findings, used);
    }
}

/// <summary>EKF warnings and state changes the autopilot logged as messages, errors or events.</summary>
internal sealed class EkfMessagesRule : IAnalysisRule
{
    private static readonly TimeSpan RepeatWindow = TimeSpan.FromSeconds(10);

    public RuleDescriptor Descriptor { get; } = new(
        "ekf.messages", 1, "ekf", "EKF warnings and state changes",
        "Reports what the autopilot itself logged about its estimator: text messages that mention the EKF/estimator, ArduPilot EKFCHECK and "
        + "EKF navigation failsafe errors, EKF primary core (lane) switches and EKF reset events. The severity follows the severity the "
        + "autopilot gave the message; repeats of the same message within 10 s are reported once with their count.",
        ["Events: log messages, ArduPilot ERR (EKFCHECK, FAILSAFE_EKFINAV, EKF_PRIMARY), ArduPilot EV (EKF_*)"],
        "Always applicable; finds nothing when the autopilot logged no such message. ArduPilot text messages carry no severity and are reported as information.");

    public Task<RuleEvaluation> EvaluateAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        var events = context.Events.Where(IsEkfEvent).Where(e => !IsCleared(e)).ToList();
        var findings = GroupRepeats(events, TextKey, RepeatWindow).Select(group =>
        {
            var first = group[0];
            var severity = first.Source == "ERR" && first.Text.StartsWith("EKF_PRIMARY", StringComparison.Ordinal)
                ? FindingSeverity.Info
                : first.Source == "ERR" ? FindingSeverity.Warning : group.Max(e => FromEventSeverity(e.Severity));
            if (first.Kind == FlightEventKind.Failsafe)
            {
                severity = FindingSeverity.Critical;
            }

            return new FindingDraft
            {
                Title = first.Source == "ERR" && first.Text.StartsWith("EKF_PRIMARY", StringComparison.Ordinal) ? "EKF primary core changed" : "EKF message",
                Description = group.Count == 1 ? $"The autopilot logged: \"{first.Text}\"." : $"The autopilot logged \"{first.Text}\" and {group.Count - 1} similar message(s) within 10 s.",
                Severity = severity,
                Confidence = FindingConfidence.High,
                StartUs = first.TimestampUs,
                EndUs = group[^1].TimestampUs,
                Signals = [$"event:{first.Source}"],
                Measured = new Dictionary<string, double> { ["occurrences"] = group.Count },
                ThresholdSource = "Reported by the autopilot; no threshold applied by this rule.",
                OccurrenceCount = group.Count,
                Limitations = first.Severity is null ? "The log gives no severity for this message; it is reported as information unless it is an error record." : null,
            };
        }).ToList();
        return Task.FromResult(RuleEvaluation.Applied(findings, ["events"]));
    }

    private static bool IsCleared(FlightEvent e) => e.Source == "ERR" && e.Data?.GetValueOrDefault("code") == "0" && !e.Text.StartsWith("EKF_PRIMARY", StringComparison.Ordinal);
}
