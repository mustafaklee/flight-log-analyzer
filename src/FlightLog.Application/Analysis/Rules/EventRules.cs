using FlightLog.Domain.Analysis;
using FlightLog.Domain.FlightData;
using static FlightLog.Application.Analysis.RuleHelpers;

namespace FlightLog.Application.Analysis.Rules;

/// <summary>Failsafes the autopilot triggered, with their duration until cleared.</summary>
internal sealed class FailsafeRule : IAnalysisRule
{
    private static readonly TimeSpan RepeatWindow = TimeSpan.FromSeconds(10);

    public RuleDescriptor Descriptor { get; } = new(
        "events.failsafe", 1, "events", "Failsafe triggered",
        "Reports every failsafe the autopilot logged (PX4 vehicle_status failsafe flag, ArduPilot ERR FAILSAFE_* records), from the moment it was "
        + "triggered until the matching \"cleared\" record, or until the end of the armed period when it was never cleared. A failsafe that "
        + "triggers again within 10 s of clearing is reported once with its count. EKF failsafes are reported by ekf.messages.",
        ["Events: failsafe records"],
        "Always applicable; finds nothing when no failsafe was logged.");

    public Task<RuleEvaluation> EvaluateAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        var failsafes = context.Events.Where(e => e.Kind == FlightEventKind.Failsafe && !IsEkfEvent(e)).ToList();
        var triggers = new List<(FlightEvent Trigger, long EndUs, bool Cleared)>();
        foreach (var e in failsafes.Where(e => !IsClear(e)))
        {
            var clear = failsafes.FirstOrDefault(c => IsClear(c) && c.TimestampUs >= e.TimestampUs && Key(c) == Key(e));
            var end = clear?.TimestampUs ?? context.Windows.FirstOrDefault(w => w.Contains(e.TimestampUs)).EndUs;
            triggers.Add((e, Math.Max(end, e.TimestampUs), clear is not null));
        }

        var groups = GroupRepeats(triggers.Select(t => t.Trigger), Key, RepeatWindow);
        var findings = groups.Select(group =>
        {
            var parts = triggers.Where(t => group.Contains(t.Trigger)).ToList();
            var first = group[0];
            var end = parts.Max(p => p.EndUs);
            var cleared = parts[^1].Cleared;
            return new FindingDraft
            {
                Title = "Failsafe triggered",
                Description = $"The autopilot logged \"{first.Text}\"" + (group.Count > 1 ? $" {group.Count} times" : "")
                    + (cleared ? $"; it was cleared after {Seconds((end - first.TimestampUs) / 1e6)}." : "; no clearing record follows in the analyzed time."),
                Severity = FindingSeverity.Critical,
                Confidence = FindingConfidence.High,
                StartUs = first.TimestampUs,
                EndUs = end,
                Signals = [$"event:{first.Source}"],
                Measured = new Dictionary<string, double> { ["occurrences"] = group.Count, ["durationS"] = Math.Round((end - first.TimestampUs) / 1e6, 2) },
                ThresholdSource = "Reported by the autopilot; no threshold applied by this rule.",
                OccurrenceCount = group.Count,
                Limitations = first.Source == "vehicle_status"
                    ? "PX4 logs only that a failsafe is active, not which one; the log messages around this time usually name the cause."
                    : null,
            };
        }).ToList();
        return Task.FromResult(RuleEvaluation.Applied(findings, ["events"]));
    }

    private static bool IsClear(FlightEvent e) =>
        e.Data?.GetValueOrDefault("active") == "false" || (e.Source == "ERR" && e.Data?.GetValueOrDefault("code") == "0");

    private static string Key(FlightEvent e) => $"{e.Source}|{e.Data?.GetValueOrDefault("subsystem")}";
}

/// <summary>Errors and high-severity messages the autopilot logged (other than EKF and failsafe ones).</summary>
internal sealed class ErrorEventsRule : IAnalysisRule
{
    private static readonly TimeSpan RepeatWindow = TimeSpan.FromSeconds(10);

    public RuleDescriptor Descriptor { get; } = new(
        "events.errors", 1, "events", "Errors reported by the autopilot",
        "Reports subsystem errors (ArduPilot ERR records other than EKF and failsafe ones, which have their own rules) and log messages with "
        + "severity error or worse. Emergency, alert and critical messages are critical; errors are warnings. The same message repeated within "
        + "10 s is reported once with its count.",
        ["Events: error records and log messages with a severity"],
        "Always applicable; finds nothing when no error was logged. ArduPilot text messages carry no severity and are therefore not included.");

    public Task<RuleEvaluation> EvaluateAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        var events = context.Events.Where(e => !IsEkfEvent(e) && (IsSubsystemError(e) || IsSevereMessage(e))).ToList();
        var findings = GroupRepeats(events, TextKey, RepeatWindow).Select(group =>
        {
            var first = group[0];
            var severity = first.Kind == FlightEventKind.Error ? ErrorSeverity(first) : group.Max(e => FromEventSeverity(e.Severity));
            return new FindingDraft
            {
                Title = first.Kind == FlightEventKind.Error ? "Subsystem error" : "Error message",
                Description = group.Count == 1 ? $"The autopilot logged: \"{first.Text}\"." : $"The autopilot logged \"{first.Text}\" and {group.Count - 1} similar message(s) within 10 s.",
                Severity = severity,
                Confidence = FindingConfidence.High,
                StartUs = first.TimestampUs,
                EndUs = group[^1].TimestampUs,
                Signals = [$"event:{first.Source}"],
                Measured = new Dictionary<string, double> { ["occurrences"] = group.Count },
                ThresholdSource = "Reported by the autopilot; no threshold applied by this rule.",
                OccurrenceCount = group.Count,
            };
        }).ToList();
        return Task.FromResult(RuleEvaluation.Applied(findings, ["events"]));
    }

    private static bool IsSubsystemError(FlightEvent e) => e.Kind == FlightEventKind.Error && e.Data?.GetValueOrDefault("code") != "0";

    private static bool IsSevereMessage(FlightEvent e) => e.Kind == FlightEventKind.Message && e.Severity is <= EventSeverity.Error;

    /// <summary>A crash check error means the autopilot detected a crash; the rest are warnings.</summary>
    private static FindingSeverity ErrorSeverity(FlightEvent e) =>
        e.Text.StartsWith("CRASH_CHECK", StringComparison.Ordinal) || e.Text.StartsWith("THRUST_LOSS_CHECK", StringComparison.Ordinal)
            ? FindingSeverity.Critical
            : FindingSeverity.Warning;
}

/// <summary>Return/land modes engaged in flight, and rapid mode switching.</summary>
internal sealed class ModeTransitionRule : IAnalysisRule
{
    private const int FlappingChanges = 4;
    private static readonly TimeSpan FlappingWindow = TimeSpan.FromSeconds(10);

    private static readonly HashSet<string> ReturnOrLandModes = new(StringComparer.Ordinal)
    {
        // ArduPilot (Copter, Plane, Rover)
        "RTL", "SMART_RTL", "AUTO_RTL", "LAND", "QLAND", "QRTL", "LOITER_ALT_QLAND",

        // PX4 navigation states
        "AUTO_LAND", "AUTO_PRECLAND", "DESCEND", "TERMINATION",
    };

    public RuleDescriptor Descriptor { get; } = new(
        "modes.transitions", 1, "modes", "Flight mode transitions",
        "Reports switches into return or landing modes while armed (information: commanded or automatic) and rapid mode switching "
        + "(4 or more mode changes within 10 s while armed, warning). ArduPilot logs a mode change reason number, which is included as measured value.",
        ["Events: mode changes, arm/disarm"],
        "Not applicable when the log has no mode changes. Without arming information the whole log is analyzed.");

    public Task<RuleEvaluation> EvaluateAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        var modes = context.Events.Where(e => e.Kind == FlightEventKind.ModeChange).ToList();
        if (modes.Count == 0)
        {
            return Task.FromResult(RuleEvaluation.NotApplicable("The log has no mode changes."));
        }

        var inFlight = modes.Where(e => InWindows(context.Windows, e.TimestampUs)).ToList();
        var findings = new List<FindingDraft>();
        foreach (var e in inFlight.Where(e => e.Data?.GetValueOrDefault("mode") is { } mode && ReturnOrLandModes.Contains(mode)))
        {
            var measured = new Dictionary<string, double>();
            if (double.TryParse(e.Data?.GetValueOrDefault("reason"), System.Globalization.NumberStyles.Float, Invariant, out var reason))
            {
                measured["modeReason"] = reason;
            }

            findings.Add(new FindingDraft
            {
                Title = $"{e.Data!["mode"]} mode engaged",
                Description = $"The vehicle switched to {e.Data!["mode"]} while armed." + (measured.Count > 0 ? $" ArduPilot logged mode change reason {Number(reason, "0")}." : ""),
                Severity = FindingSeverity.Info,
                Confidence = FindingConfidence.High,
                StartUs = e.TimestampUs,
                EndUs = e.TimestampUs,
                Signals = [$"event:{e.Source}"],
                Measured = measured,
                ThresholdSource = "Mode list of this rule (return and landing modes).",
                Limitations = Limitations(
                    measured.Count > 0 ? null : "The log does not say whether the pilot, a mission or a failsafe requested the mode.",
                    context.WindowLimitation),
            });
        }

        var windowUs = (long)(FlappingWindow.TotalMilliseconds * 1000);
        var flapping = new List<ConditionInterval>();
        for (var i = 0; i + FlappingChanges - 1 < inFlight.Count; i++)
        {
            var last = inFlight[i + FlappingChanges - 1];
            if (last.TimestampUs - inFlight[i].TimestampUs <= windowUs)
            {
                flapping.Add(new ConditionInterval(inFlight[i].TimestampUs, last.TimestampUs, FlappingChanges, 0, 0));
            }
        }

        foreach (var merged in SeriesMath.Merge(flapping, TimeSpan.Zero))
        {
            var changes = inFlight.Where(e => e.TimestampUs >= merged.StartUs && e.TimestampUs <= merged.EndUs).ToList();
            findings.Add(new FindingDraft
            {
                Title = "Rapid mode switching",
                Description = $"The flight mode changed {changes.Count} times in {Seconds(merged.DurationSeconds)}: "
                    + string.Join(" → ", changes.Select(c => c.Data?.GetValueOrDefault("mode") ?? c.Text)) + ".",
                Severity = FindingSeverity.Warning,
                Confidence = FindingConfidence.High,
                StartUs = merged.StartUs,
                EndUs = merged.EndUs,
                Signals = [$"event:{changes[0].Source}"],
                Measured = new Dictionary<string, double> { ["modeChanges"] = changes.Count, ["durationS"] = Math.Round(merged.DurationSeconds, 2) },
                Threshold = new Dictionary<string, double> { ["modeChanges"] = FlappingChanges, ["windowS"] = FlappingWindow.TotalSeconds },
                ThresholdSource = "Heuristic of this rule (4 changes in 10 s).",
                OccurrenceCount = changes.Count,
                Limitations = context.WindowLimitation,
            });
        }

        return Task.FromResult(RuleEvaluation.Applied([.. findings.OrderBy(f => f.StartUs)], ["events"]));
    }
}

/// <summary>Parameters changed while the vehicle was armed.</summary>
internal sealed class ParameterChangeRule : IAnalysisRule
{
    private static readonly TimeSpan GroupWindow = TimeSpan.FromSeconds(10);

    private static bool IsBookkeeping(string name) => name.StartsWith("STAT_", StringComparison.Ordinal);

    public RuleDescriptor Descriptor { get; } = new(
        "parameters.changes", 1, "parameters", "Parameters changed in flight",
        "Reports parameter changes the log records while the vehicle was armed, grouping changes within 10 s. The measured values are the new "
        + "values; the description gives old and new value. ArduPilot STAT_* counters (run time, flight time, boot count) are ignored: "
        + "the autopilot updates them itself.",
        ["Parameters: values at log start and logged changes"],
        "Not applicable when the log contains no parameters. Without arming information every change after log start is reported.");

    public Task<RuleEvaluation> EvaluateAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        if (context.InitialParameters.Count == 0 && context.ParameterChanges.Count == 0)
        {
            return Task.FromResult(RuleEvaluation.NotApplicable("The log contains no parameters."));
        }

        var previous = new Dictionary<string, double>(context.InitialParameters, StringComparer.Ordinal);
        var changes = new List<(FlightParameter Change, double? Old)>();
        foreach (var change in context.ParameterChanges)
        {
            var old = previous.TryGetValue(change.Name, out var value) ? value : (double?)null;
            previous[change.Name] = change.Value;
            if (old != change.Value && !IsBookkeeping(change.Name) && InWindows(context.Windows, change.TimestampUs!.Value))
            {
                changes.Add((change, old));
            }
        }

        var windowUs = (long)(GroupWindow.TotalMilliseconds * 1000);
        var groups = new List<List<(FlightParameter Change, double? Old)>>();
        foreach (var c in changes)
        {
            if (groups.Count > 0 && c.Change.TimestampUs!.Value - groups[^1][^1].Change.TimestampUs!.Value <= windowUs)
            {
                groups[^1].Add(c);
            }
            else
            {
                groups.Add([c]);
            }
        }

        var findings = groups.Select(group => new FindingDraft
        {
            Title = group.Count == 1 ? $"Parameter {group[0].Change.Name} changed in flight" : $"{group.Count} parameters changed in flight",
            Description = "Changed while armed: " + string.Join(", ", group.Select(c =>
                $"{c.Change.Name} {(c.Old is { } old ? Number(old, "0.######") : "?")} → {Number(c.Change.Value, "0.######")}")) + ".",
            Severity = FindingSeverity.Info,
            Confidence = FindingConfidence.High,
            StartUs = group[0].Change.TimestampUs!.Value,
            EndUs = group[^1].Change.TimestampUs!.Value,
            Signals = [.. group.Select(c => $"parameter:{c.Change.Name}").Distinct()],
            Measured = group.GroupBy(c => c.Change.Name).ToDictionary(g => g.Key, g => g.Last().Change.Value),
            ThresholdSource = "Any logged change while armed.",
            OccurrenceCount = group.Count,
            Limitations = context.WindowLimitation,
        }).ToList();
        return Task.FromResult(RuleEvaluation.Applied(findings, ["parameters"]));
    }
}
