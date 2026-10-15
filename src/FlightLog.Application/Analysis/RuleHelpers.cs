using System.Globalization;
using System.Text.RegularExpressions;
using FlightLog.Domain.Analysis;
using FlightLog.Domain.FlightData;

namespace FlightLog.Application.Analysis;

/// <summary>Shared building blocks of the rules.</summary>
internal static partial class RuleHelpers
{
    public static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>Intersects two sets of ranges (each sorted, non-overlapping).</summary>
    public static List<TimeRange> Intersect(IReadOnlyList<TimeRange> a, IReadOnlyList<TimeRange> b)
    {
        var result = new List<TimeRange>();
        foreach (var x in a)
        {
            foreach (var y in b)
            {
                var from = Math.Max(x.StartUs, y.StartUs);
                var to = Math.Min(x.EndUs, y.EndUs);
                if (to > from)
                {
                    result.Add(new TimeRange(from, to));
                }
            }
        }

        return [.. result.OrderBy(r => r.StartUs)];
    }

    /// <summary>The windows, each starting no earlier than <paramref name="fromUs"/>.</summary>
    public static List<TimeRange> From(IReadOnlyList<TimeRange> windows, long fromUs) =>
        [.. windows.Where(w => w.EndUs > fromUs).Select(w => new TimeRange(Math.Max(w.StartUs, fromUs), w.EndUs))];

    /// <summary>Whether an event lies in one of the windows.</summary>
    public static bool InWindows(IReadOnlyList<TimeRange> windows, long t) => windows.Any(w => w.Contains(t));

    /// <summary>Medium with good coverage and a documented or configured threshold; Low otherwise.</summary>
    public static FindingConfidence MeasuredConfidence(double coverage, bool documentedThreshold) =>
        documentedThreshold && coverage >= 0.8 ? FindingConfidence.Medium : FindingConfidence.Low;

    /// <summary>Joins limitation sentences, skipping empty ones.</summary>
    public static string? Limitations(params string?[] parts)
    {
        var text = string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        return text.Length == 0 ? null : text;
    }

    public static string Seconds(double seconds) => seconds.ToString(seconds < 10 ? "0.0" : "0", Invariant) + " s";

    public static string Number(double value, string format = "0.##") => value.ToString(format, Invariant);

    /// <summary>
    /// Names of the existing signals <c>{message}[#n].{field}</c> for every instance of a raw message, ordered by
    /// instance (instance 0 has no suffix).
    /// </summary>
    public static List<string> Instances(AnalysisContext context, string message, string field)
    {
        var plain = $"{message}.{field}";
        var prefix = $"{message}#";
        var suffix = $".{field}";
        return [.. context.SignalNames
            .Where(n => n == plain || (n.StartsWith(prefix, StringComparison.Ordinal) && n.EndsWith(suffix, StringComparison.Ordinal)
                && int.TryParse(n.AsSpan(prefix.Length, n.Length - prefix.Length - suffix.Length), NumberStyles.None, Invariant, out _)))
            .OrderBy(n => n.Length).ThenBy(n => n, StringComparer.Ordinal)];
    }

    /// <summary>Groups events with the same key that follow each other within <paramref name="window"/>.</summary>
    public static List<List<FlightEvent>> GroupRepeats(IEnumerable<FlightEvent> events, Func<FlightEvent, string> key, TimeSpan window)
    {
        var windowUs = (long)(window.TotalMilliseconds * 1000);
        var groups = new List<List<FlightEvent>>();
        var open = new Dictionary<string, List<FlightEvent>>(StringComparer.Ordinal);
        foreach (var e in events.OrderBy(e => e.TimestampUs))
        {
            var k = key(e);
            if (open.TryGetValue(k, out var group) && e.TimestampUs - group[^1].TimestampUs <= windowUs)
            {
                group.Add(e);
                continue;
            }

            group = [e];
            open[k] = group;
            groups.Add(group);
        }

        return groups;
    }

    /// <summary>Message text with numbers replaced, so that "IMU0" and "IMU1" variants of one message group together.</summary>
    public static string TextKey(FlightEvent e) => $"{e.Kind}|{e.Source}|{Digits().Replace(e.Text, "#")}";

    /// <summary>EKF/estimator related events; they belong to the EKF rule and are skipped by the generic event rule.</summary>
    public static bool IsEkfEvent(FlightEvent e) =>
        (e.Source == "ERR" && (e.Text.StartsWith("EKFCHECK", StringComparison.Ordinal) || e.Text.StartsWith("FAILSAFE_EKFINAV", StringComparison.Ordinal)
            || e.Text.StartsWith("EKF_PRIMARY", StringComparison.Ordinal)))
        || (e.Kind == FlightEventKind.Event && e.Text.StartsWith("EKF_", StringComparison.Ordinal))
        || (e.Kind == FlightEventKind.Message && EkfText().IsMatch(e.Text));

    /// <summary>Severity of a finding about a logged event, from the syslog severity the autopilot gave it.</summary>
    public static FindingSeverity FromEventSeverity(EventSeverity? severity) => severity switch
    {
        EventSeverity.Emergency or EventSeverity.Alert or EventSeverity.Critical => FindingSeverity.Critical,
        EventSeverity.Error or EventSeverity.Warning => FindingSeverity.Warning,
        _ => FindingSeverity.Info,
    };

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();

    [GeneratedRegex(@"\b(EKF\d?|ekf2|ecl|[Ee]stimator|[Yy]aw reset|[Ii]nnovation|[Ll]ane switch)", RegexOptions.CultureInvariant)]
    private static partial Regex EkfText();
}
