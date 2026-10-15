using FlightLog.Domain.Analysis;
using FlightLog.Domain.Common;
using static FlightLog.Application.Analysis.RuleHelpers;

namespace FlightLog.Application.Analysis.Rules;

/// <summary>Battery voltage below the low/critical voltage configured on the vehicle.</summary>
internal sealed class BatteryVoltageRule : IAnalysisRule
{
    private const string Voltage = "battery.voltage";
    private static readonly TimeSpan MaxSampleGap = TimeSpan.FromSeconds(5);

    public RuleDescriptor Descriptor { get; } = new(
        "battery.voltage", 1, "battery", "Battery voltage below configured limit",
        "Reports periods in which the battery voltage stays below the limit configured on the vehicle. ArduPilot: BATT_LOW_VOLT (warning) and "
        + "BATT_CRT_VOLT (critical) for at least BATT_LOW_TIMER seconds (10 s when not logged), as ArduPilot's own battery failsafe does. "
        + "PX4: BAT_V_EMPTY (or BAT1_V_EMPTY) × cell count for at least 5 s (warning).",
        [Voltage, "ArduPilot: BATT_LOW_VOLT, BATT_CRT_VOLT, BATT_LOW_TIMER", "PX4: BAT_V_EMPTY/BAT1_V_EMPTY, BAT_N_CELLS/BAT1_N_CELLS"],
        "Not applicable without battery voltage or when no voltage limit is configured (parameter missing or 0). Only armed periods are analyzed when the log records arming.");

    public async Task<RuleEvaluation> EvaluateAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        var voltage = await context.SignalAsync(Voltage, cancellationToken);
        if (voltage is null)
        {
            return RuleEvaluation.NotApplicable("The log has no battery voltage.");
        }

        var limits = Limits(context);
        if (limits.Count == 0)
        {
            return RuleEvaluation.NotApplicable("No battery voltage limit is configured in the logged parameters.");
        }

        var coverage = SeriesMath.Coverage(voltage, context.Windows, MaxSampleGap);
        var findings = new List<FindingDraft>();
        var covered = new List<TimeRange>();

        // Critical first, so that the warning does not report the same time again.
        foreach (var limit in limits.OrderByDescending(l => l.Severity))
        {
            foreach (var i in SeriesMath.FindIntervals(voltage, v => v < limit.Volts, context.Windows, limit.MinDuration, TimeSpan.FromSeconds(5), MaxSampleGap))
            {
                if (covered.Any(c => c.StartUs <= i.StartUs && i.EndUs <= c.EndUs))
                {
                    continue;
                }

                covered.Add(i.Range);
                findings.Add(new FindingDraft
                {
                    Title = limit.Severity == FindingSeverity.Critical ? "Battery voltage critical" : "Battery voltage low",
                    Description = $"Battery voltage stayed below {Number(limit.Volts)} V for {Seconds(i.DurationSeconds)} (lowest {Number(i.Min)} V).",
                    Severity = limit.Severity,
                    Confidence = MeasuredConfidence(coverage, documentedThreshold: true),
                    StartUs = i.StartUs,
                    EndUs = i.EndUs,
                    Signals = [Voltage],
                    Measured = new Dictionary<string, double> { ["minVoltage"] = i.Min, ["durationS"] = Math.Round(i.DurationSeconds, 2) },
                    Threshold = new Dictionary<string, double> { ["voltage"] = limit.Volts, ["minDurationS"] = limit.MinDuration.TotalSeconds },
                    ThresholdSource = limit.Source,
                    DataCoverage = coverage,
                    Limitations = Limitations(
                        "The measured voltage includes the sag under load; the autopilot may compare a load-compensated (resting) voltage instead.",
                        context.WindowLimitation),
                });
            }
        }

        return RuleEvaluation.Applied([.. findings.OrderBy(f => f.StartUs)], [Voltage]);
    }

    private static List<(double Volts, FindingSeverity Severity, TimeSpan MinDuration, string Source)> Limits(AnalysisContext context)
    {
        var result = new List<(double, FindingSeverity, TimeSpan, string)>();
        if (context.Autopilot == Autopilot.ArduPilot)
        {
            var timer = context.Parameter("BATT_LOW_TIMER") is { } t && t > 0 ? TimeSpan.FromSeconds(t) : TimeSpan.FromSeconds(10);
            if (context.Parameter("BATT_LOW_VOLT") is { } low && low > 0)
            {
                result.Add((low, FindingSeverity.Warning, timer, "Vehicle parameters BATT_LOW_VOLT and BATT_LOW_TIMER"));
            }

            if (context.Parameter("BATT_CRT_VOLT") is { } critical && critical > 0)
            {
                result.Add((critical, FindingSeverity.Critical, timer, "Vehicle parameters BATT_CRT_VOLT and BATT_LOW_TIMER"));
            }
        }
        else if (context.Autopilot == Autopilot.Px4)
        {
            var (emptyName, empty) = First(context, "BAT1_V_EMPTY", "BAT_V_EMPTY");
            var (cellsName, cells) = First(context, "BAT1_N_CELLS", "BAT_N_CELLS");
            if (empty is > 0 && cells is > 0)
            {
                result.Add((empty.Value * cells.Value, FindingSeverity.Warning, TimeSpan.FromSeconds(5), $"Vehicle parameters {emptyName} × {cellsName}"));
            }
        }

        return result;
    }

    private static (string Name, double? Value) First(AnalysisContext context, params string[] names)
    {
        foreach (var name in names)
        {
            if (context.Parameter(name) is { } value)
            {
                return (name, value);
            }
        }

        return (names[^1], null);
    }
}

/// <summary>Remaining battery capacity below the low/critical level configured on the vehicle.</summary>
internal sealed class BatteryRemainingRule : IAnalysisRule
{
    private const string Remaining = "battery.remaining";
    private static readonly TimeSpan MinDuration = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MaxSampleGap = TimeSpan.FromSeconds(5);

    public RuleDescriptor Descriptor { get; } = new(
        "battery.remaining", 1, "battery", "Battery capacity below configured level",
        "Reports when the remaining battery capacity the autopilot estimates falls below the levels configured on the vehicle for at least 3 s. "
        + "PX4: BAT_LOW_THR (warning) and BAT_CRIT_THR (critical), fractions of capacity. ArduPilot: BATT_LOW_MAH and BATT_CRT_MAH as a share of BATT_CAPACITY.",
        [Remaining, "PX4: BAT_LOW_THR, BAT_CRIT_THR", "ArduPilot: BATT_CAPACITY, BATT_LOW_MAH, BATT_CRT_MAH"],
        "Not applicable without remaining capacity in the log or without configured levels. Only armed periods are analyzed when the log records arming.");

    public async Task<RuleEvaluation> EvaluateAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        var remaining = await context.SignalAsync(Remaining, cancellationToken);
        if (remaining is null)
        {
            return RuleEvaluation.NotApplicable("The log has no remaining battery capacity.");
        }

        var levels = Levels(context);
        if (levels.Count == 0)
        {
            return RuleEvaluation.NotApplicable("No low-battery level is configured in the logged parameters.");
        }

        var coverage = SeriesMath.Coverage(remaining, context.Windows, MaxSampleGap);
        var findings = new List<FindingDraft>();
        foreach (var level in levels.OrderByDescending(l => l.Severity))
        {
            // Remaining capacity only falls during a flight: report the first time each level is crossed.
            var first = SeriesMath.FindIntervals(remaining, v => v < level.Percent, context.Windows, MinDuration, TimeSpan.FromSeconds(30), MaxSampleGap).FirstOrDefault();
            if (first is null)
            {
                continue;
            }

            findings.Add(new FindingDraft
            {
                Title = level.Severity == FindingSeverity.Critical ? "Battery capacity critical" : "Battery capacity low",
                Description = $"The estimated remaining capacity fell below {Number(level.Percent, "0.#")} % (lowest {Number(first.Min, "0.#")} %).",
                Severity = level.Severity,
                Confidence = MeasuredConfidence(coverage, documentedThreshold: true),
                StartUs = first.StartUs,
                EndUs = first.EndUs,
                Signals = [Remaining],
                Measured = new Dictionary<string, double> { ["minRemainingPercent"] = first.Min, ["durationS"] = Math.Round(first.DurationSeconds, 2) },
                Threshold = new Dictionary<string, double> { ["remainingPercent"] = level.Percent, ["minDurationS"] = MinDuration.TotalSeconds },
                ThresholdSource = level.Source,
                DataCoverage = coverage,
                Limitations = Limitations("Remaining capacity is the autopilot's estimate; its accuracy depends on the configured capacity and current sensor calibration.", context.WindowLimitation),
            });
        }

        return RuleEvaluation.Applied([.. findings.OrderBy(f => f.StartUs)], [Remaining]);
    }

    // Parameters are float32 on the vehicle; round so 0.07 reads as 7 %, not 7.000000000000001 %.
    private static double Percent(double fraction) => Math.Round(fraction * 100, 4);

    private static List<(double Percent, FindingSeverity Severity, string Source)> Levels(AnalysisContext context)
    {
        var result = new List<(double, FindingSeverity, string)>();
        if (context.Autopilot == Autopilot.Px4)
        {
            if (context.Parameter("BAT_LOW_THR") is { } low && low > 0)
            {
                result.Add((Percent(low), FindingSeverity.Warning, "Vehicle parameter BAT_LOW_THR"));
            }

            if (context.Parameter("BAT_CRIT_THR") is { } critical && critical > 0)
            {
                result.Add((Percent(critical), FindingSeverity.Critical, "Vehicle parameter BAT_CRIT_THR"));
            }
        }
        else if (context.Autopilot == Autopilot.ArduPilot && context.Parameter("BATT_CAPACITY") is { } capacity && capacity > 0)
        {
            if (context.Parameter("BATT_LOW_MAH") is { } low && low > 0)
            {
                result.Add((Percent(low / capacity), FindingSeverity.Warning, "Vehicle parameters BATT_LOW_MAH ÷ BATT_CAPACITY"));
            }

            if (context.Parameter("BATT_CRT_MAH") is { } critical && critical > 0)
            {
                result.Add((Percent(critical / capacity), FindingSeverity.Critical, "Vehicle parameters BATT_CRT_MAH ÷ BATT_CAPACITY"));
            }
        }

        return result;
    }
}
