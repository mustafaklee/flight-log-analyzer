using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FlightLog.Application.Analysis;
using FlightLog.Domain.Analysis;
using FlightLog.Domain.Common;
using FlightLog.Domain.FlightData;

namespace FlightLog.Application.Ai;

/// <summary>Statistics of one signal over a time range, computed from the stored samples.</summary>
public sealed record SignalStatistic(string Signal, string? Unit, string? FindingId, long StartUs, long EndUs, long Samples, double Min, double Max, double Mean);

/// <summary>Everything the context is built from. Loaded by <see cref="FlightContextLoader"/>; plain data so the builder is testable.</summary>
public sealed record FlightContextInput(
    Autopilot Autopilot,
    string? VehicleType,
    string? FirmwareVersion,
    string? Hardware,
    TimeRange Log,
    IReadOnlyList<TimeRange> ArmedIntervals,
    Guid AnalysisRunId,
    string EngineVersion,
    IReadOnlyList<AnalysisFinding> Findings,
    IReadOnlyList<AnalysisRuleResult> RuleResults,
    IReadOnlyList<FlightEvent> Events,
    IReadOnlyDictionary<string, double> InitialParameters,
    IReadOnlyList<FlightParameter> ParameterChanges,
    IReadOnlyList<SignalStatistic> SignalStatistics);

public enum EvidenceKind
{
    Finding = 1,
    Event = 2,
    Signal = 3,
    Parameter = 4,
    Rule = 5,
}

/// <summary>An item of the context the model may cite. Times are on the log clock (µs).</summary>
public sealed record EvidenceItem(string Ref, EvidenceKind Kind, string Label, long? StartUs, long? EndUs, Guid? FindingId, string? Signal);

/// <summary>The context sent to the model and what the report validation needs to check the answer against it.</summary>
public sealed record FlightContext(
    string Json,
    string Hash,
    IReadOnlyDictionary<string, string> Summary,
    IReadOnlyDictionary<string, EvidenceItem> Evidence,
    IReadOnlyList<double> Numbers,
    long LogStartUs);

public sealed record FlightContextLimits(int MaxCharacters, int MaxFindings, int MaxEvents, int MaxParameterChanges, int MaxSignalStatistics)
{
    public static FlightContextLimits From(AiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new(options.MaxContextCharacters, options.MaxFindings, options.MaxEvents, options.MaxParameterChanges, options.MaxSignalStatistics);
    }
}

/// <summary>
/// Builds the model's input from a flight's rule analysis (ADR-0008): findings, rule outcomes, signal statistics,
/// selected events and parameter changes, each with a stable reference (F1, R1, S1, E1, P1) the report must cite.
/// The output is deterministic for the same data: same references, same text, same hash. The raw log, GPS
/// coordinates and user-entered texts (flight name, notes) are never included; texts from the log go into a
/// separate section marked as untrusted. When the context would exceed the size limit, lower-priority items are
/// left out and the omitted counts are part of the context, so the model can state the limitation.
/// </summary>
public static partial class FlightContextBuilder
{
    public const string Version = "flight-context-v1";

    private const int MaxTextLength = 240;

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = false };

    public static FlightContext Build(FlightContextInput input, FlightContextLimits limits)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(limits);

        var current = limits;
        while (true)
        {
            var context = Render(input, current);
            if (context.Json.Length <= limits.MaxCharacters || Shrink(current) is not { } smaller)
            {
                return context;
            }

            current = smaller;
        }
    }

    /// <summary>Halves the least important part that is still large; null when nothing can be removed anymore.</summary>
    private static FlightContextLimits? Shrink(FlightContextLimits l) =>
        l.MaxEvents > 10 ? l with { MaxEvents = l.MaxEvents / 2 }
        : l.MaxParameterChanges > 5 ? l with { MaxParameterChanges = l.MaxParameterChanges / 2 }
        : l.MaxSignalStatistics > 10 ? l with { MaxSignalStatistics = l.MaxSignalStatistics / 2 }
        : l.MaxFindings > 10 ? l with { MaxFindings = l.MaxFindings / 2 }
        : l.MaxEvents > 0 ? l with { MaxEvents = 0 }
        : null;

    private static FlightContext Render(FlightContextInput input, FlightContextLimits limits)
    {
        var numbers = new SortedSet<double>();
        var evidence = new Dictionary<string, EvidenceItem>(StringComparer.Ordinal);
        var logStart = input.Log.StartUs;

        double Num(double value)
        {
            var rounded = RoundForContext(value);
            numbers.Add(rounded);
            return rounded;
        }

        double Seconds(long us) => Num((us - logStart) / 1e6);

        // Findings: the most severe first when there are too many, then presented in time order.
        var findings = input.Findings
            .OrderByDescending(f => f.Severity).ThenBy(f => f.StartUs).ThenBy(f => f.Id)
            .Take(Math.Max(0, limits.MaxFindings))
            .OrderBy(f => f.StartUs).ThenByDescending(f => f.Severity).ThenBy(f => f.Id)
            .ToList();
        var findingRefs = new Dictionary<Guid, string>();
        var findingNodes = new JsonArray();
        foreach (var f in findings)
        {
            var id = $"F{findingRefs.Count + 1}";
            findingRefs[f.Id] = id;
            var chartSignal = f.Signals.FirstOrDefault(IsChartSignal);
            evidence[id] = new EvidenceItem(id, EvidenceKind.Finding, Clean(f.Title), f.StartUs, f.EndUs, f.Id, chartSignal);
            findingNodes.Add(new JsonObject
            {
                ["ref"] = id,
                ["rule"] = $"{f.RuleId} v{f.RuleVersion}",
                ["category"] = f.Category,
                ["severity"] = f.Severity.ToString().ToLowerInvariant(),
                ["confidence"] = f.Confidence.ToString().ToLowerInvariant(),
                ["title"] = Clean(f.Title),
                ["description"] = Clean(f.Description),
                ["startS"] = Seconds(f.StartUs),
                ["endS"] = Seconds(f.EndUs),
                ["durationS"] = Num((f.EndUs - f.StartUs) / 1e6),
                ["signals"] = new JsonArray([.. f.Signals.Order(StringComparer.Ordinal).Select(s => (JsonNode)JsonValue.Create(Clean(s))!)]),
                ["measured"] = NumberMap(f.Measured, Num),
                ["thresholds"] = NumberMap(f.Threshold, Num),
                ["thresholdSource"] = Clean(f.ThresholdSource),
                ["dataCoverage"] = f.DataCoverage is { } c ? Num(c) : null,
                ["occurrences"] = f.OccurrenceCount,
                ["limitations"] = f.Limitations is { } lim ? Clean(lim) : null,
            });
            numbers.Add(f.OccurrenceCount);
            if (f.DataCoverage is { } coverage)
            {
                // Coverage is a share; the report may give it as a percentage.
                numbers.Add(RoundForContext(coverage * 100));
            }
        }

        // Rule outcomes: also "applied, nothing found", which supports statements that something looked normal.
        var ruleNodes = new JsonArray();
        var ruleIndex = 0;
        foreach (var r in input.RuleResults.OrderBy(r => r.RuleId, StringComparer.Ordinal))
        {
            var id = $"R{++ruleIndex}";
            evidence[id] = new EvidenceItem(id, EvidenceKind.Rule, $"{r.RuleId}: {r.Status}", null, null, null, null);
            ruleNodes.Add(new JsonObject
            {
                ["ref"] = id,
                ["rule"] = $"{r.RuleId} v{r.RuleVersion}",
                ["status"] = r.Status switch
                {
                    RuleResultStatus.Applied => "applied",
                    RuleResultStatus.NotApplicable => "not_applicable",
                    _ => "failed",
                },
                ["findings"] = r.FindingCount,
                ["reason"] = r.Reason is { } reason ? Clean(reason) : null,
                ["signalsRead"] = new JsonArray([.. r.Signals.Order(StringComparer.Ordinal).Select(s => (JsonNode)JsonValue.Create(Clean(s))!)]),
            });
            numbers.Add(r.FindingCount);
        }

        // Signal statistics: flight-wide ones first, then those of finding windows, most severe findings first.
        var severityOf = findings.ToDictionary(f => f.Id.ToString(), f => f.Severity);
        var stats = input.SignalStatistics
            .Where(s => s.FindingId is null || severityOf.ContainsKey(s.FindingId))
            .OrderBy(s => s.FindingId is null ? 0 : 1)
            .ThenByDescending(s => s.FindingId is null ? FindingSeverity.Info : severityOf[s.FindingId])
            .ThenBy(s => s.StartUs).ThenBy(s => s.Signal, StringComparer.Ordinal)
            .Take(Math.Max(0, limits.MaxSignalStatistics))
            .OrderBy(s => s.FindingId is null ? 0 : 1).ThenBy(s => s.StartUs).ThenBy(s => s.Signal, StringComparer.Ordinal)
            .ToList();
        var statNodes = new JsonArray();
        foreach (var s in stats)
        {
            var id = $"S{statNodes.Count + 1}";
            var findingRef = s.FindingId is { } fid && Guid.TryParse(fid, out var g) ? findingRefs.GetValueOrDefault(g) : null;
            var scope = findingRef is null ? "analyzed flight time" : $"window around {findingRef}";
            evidence[id] = new EvidenceItem(id, EvidenceKind.Signal, $"{s.Signal} ({scope})", s.StartUs, s.EndUs, null, s.Signal);
            statNodes.Add(new JsonObject
            {
                ["ref"] = id,
                ["signal"] = Clean(s.Signal),
                ["unit"] = s.Unit,
                ["scope"] = scope,
                ["startS"] = Seconds(s.StartUs),
                ["endS"] = Seconds(s.EndUs),
                ["samples"] = s.Samples,
                ["min"] = Num(s.Min),
                ["max"] = Num(s.Max),
                ["mean"] = Num(s.Mean),
            });
            numbers.Add(s.Samples);
        }

        // Events: failsafes, errors, arming and mode changes before ordinary messages; then in time order.
        var events = input.Events
            .OrderBy(EventPriority).ThenBy(e => e.TimestampUs).ThenBy(e => e.Id)
            .Take(Math.Max(0, limits.MaxEvents))
            .OrderBy(e => e.TimestampUs).ThenBy(e => e.Id)
            .ToList();
        var eventNodes = new JsonArray();
        var untrusted = new JsonObject();
        foreach (var e in events)
        {
            var id = $"E{eventNodes.Count + 1}";
            evidence[id] = new EvidenceItem(id, EvidenceKind.Event, $"{e.Kind} ({Clean(e.Source)})", e.TimestampUs, e.TimestampUs, null, null);
            eventNodes.Add(new JsonObject
            {
                ["ref"] = id,
                ["timeS"] = Seconds(e.TimestampUs),
                ["kind"] = e.Kind.ToString(),
                ["severity"] = e.Severity?.ToString(),
                ["source"] = Clean(e.Source),
            });
            if (!string.IsNullOrWhiteSpace(e.Text))
            {
                untrusted[id] = Clean(e.Text);
            }
        }

        // Parameters: values at log start that thresholds refer to, and changes during the log.
        var parameterNodes = new JsonArray();
        var named = findings.SelectMany(f => ParameterName().Matches(f.ThresholdSource).Select(m => m.Value))
            .Where(input.InitialParameters.ContainsKey)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        foreach (var name in named)
        {
            var id = $"P{parameterNodes.Count + 1}";
            var value = input.InitialParameters[name];
            evidence[id] = new EvidenceItem(id, EvidenceKind.Parameter, $"{name} = {Format(value)}", null, null, null, null);
            parameterNodes.Add(new JsonObject { ["ref"] = id, ["name"] = name, ["value"] = Num(value), ["at"] = "log start" });
        }

        var changes = input.ParameterChanges.Where(p => p.TimestampUs is not null)
            .OrderBy(p => p.TimestampUs).ThenBy(p => p.Name, StringComparer.Ordinal)
            .Take(Math.Max(0, limits.MaxParameterChanges)).ToList();
        foreach (var p in changes)
        {
            var id = $"P{parameterNodes.Count + 1}";
            evidence[id] = new EvidenceItem(id, EvidenceKind.Parameter, $"{Clean(p.Name)} = {Format(p.Value)}", p.TimestampUs, p.TimestampUs, null, null);
            parameterNodes.Add(new JsonObject
            {
                ["ref"] = id,
                ["name"] = Clean(p.Name),
                ["value"] = Num(p.Value),
                ["defaultValue"] = p.DefaultValue is { } d ? Num(d) : null,
                ["timeS"] = Seconds(p.TimestampUs!.Value),
            });
        }

        var armedSeconds = input.ArmedIntervals.Sum(a => a.DurationSeconds);
        var omitted = new JsonObject
        {
            ["findings"] = input.Findings.Count - findings.Count,
            ["events"] = input.Events.Count - events.Count,
            ["parameterChanges"] = input.ParameterChanges.Count(p => p.TimestampUs is not null) - changes.Count,
            ["signalStatistics"] = input.SignalStatistics.Count - stats.Count,
        };
        var root = new JsonObject
        {
            ["contextVersion"] = Version,
            ["timeBase"] = "All times are seconds from the first sample of the log (t=0).",
            ["flight"] = new JsonObject
            {
                ["autopilot"] = input.Autopilot.ToString(),
                ["vehicleType"] = input.VehicleType is { } v ? Clean(v) : null,
                ["firmware"] = input.FirmwareVersion is { } fw ? Clean(fw) : null,
                ["hardware"] = input.Hardware is { } hw ? Clean(hw) : null,
                ["logDurationS"] = Num(input.Log.DurationSeconds),
                ["armedPeriods"] = new JsonArray([.. input.ArmedIntervals.Select(a => (JsonNode)new JsonObject { ["startS"] = Seconds(a.StartUs), ["endS"] = Seconds(a.EndUs) })]),
                ["armedTotalS"] = input.ArmedIntervals.Count > 0 ? Num(armedSeconds) : null,
                ["armingKnown"] = input.ArmedIntervals.Count > 0,
            },
            ["ruleAnalysis"] = new JsonObject
            {
                ["engineVersion"] = input.EngineVersion,
                ["findingsTotal"] = input.Findings.Count,
                ["rules"] = ruleNodes,
            },
            ["findings"] = findingNodes,
            ["signalStatistics"] = statNodes,
            ["events"] = eventNodes,
            ["parameters"] = parameterNodes,
            ["omittedForSize"] = omitted,
            ["untrustedLogText"] = untrusted,
        };
        numbers.Add(input.Findings.Count);
        numbers.Add(input.ArmedIntervals.Count);

        var json = root.ToJsonString(WriteOptions);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        var summary = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["contextVersion"] = Version,
            ["analysisRunId"] = input.AnalysisRunId.ToString(),
            ["engineVersion"] = input.EngineVersion,
            ["logStartUs"] = logStart.ToString(CultureInfo.InvariantCulture),
            ["findings"] = Count(findings.Count, input.Findings.Count),
            ["rules"] = input.RuleResults.Count.ToString(CultureInfo.InvariantCulture),
            ["signalStatistics"] = Count(stats.Count, input.SignalStatistics.Count),
            ["events"] = Count(events.Count, input.Events.Count),
            ["parameters"] = parameterNodes.Count.ToString(CultureInfo.InvariantCulture),
            ["characters"] = json.Length.ToString(CultureInfo.InvariantCulture),
        };
        return new FlightContext(json, hash, summary, evidence, [.. numbers], logStart);
    }

    /// <summary>Rounding used for every number in the context; the report's numbers are checked against these.</summary>
    public static double RoundForContext(double value)
    {
        if (!double.IsFinite(value))
        {
            return 0;
        }

        var abs = Math.Abs(value);
        return Math.Round(value, abs >= 100 ? 1 : abs >= 1 ? 2 : 4, MidpointRounding.AwayFromZero);
    }

    /// <summary>Removes control characters and limits the length of a text that may come from the log.</summary>
    public static string Clean(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder(Math.Min(text.Length, MaxTextLength));
        var lastWasSpace = false;
        foreach (var ch in text)
        {
            var c = char.IsControl(ch) || char.IsWhiteSpace(ch) ? ' ' : ch;
            if (c == ' ' && lastWasSpace)
            {
                continue;
            }

            lastWasSpace = c == ' ';
            builder.Append(c);
            if (builder.Length >= MaxTextLength)
            {
                builder.Append('…');
                break;
            }
        }

        return builder.ToString().Trim();
    }

    private static bool IsChartSignal(string signal) => !signal.Contains(':', StringComparison.Ordinal);

    private static int EventPriority(FlightEvent e) => e.Kind switch
    {
        FlightEventKind.Failsafe => 0,
        FlightEventKind.Error => 1,
        _ when e.Severity is <= EventSeverity.Critical => 1,
        FlightEventKind.Armed or FlightEventKind.Disarmed => 2,
        FlightEventKind.ModeChange => 3,
        _ when e.Severity is EventSeverity.Error or EventSeverity.Warning => 4,
        FlightEventKind.Dropout => 4,
        FlightEventKind.Event => 5,
        _ when e.Severity is EventSeverity.Debug => 7,
        _ => 6,
    };

    private static JsonObject NumberMap(IReadOnlyDictionary<string, double> values, Func<double, double> num)
    {
        var node = new JsonObject();
        foreach (var (key, value) in values.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            node[key] = num(value);
        }

        return node;
    }

    private static string Count(int included, int total) =>
        included == total ? total.ToString(CultureInfo.InvariantCulture) : $"{included}/{total}";

    private static string Format(double value) => RoundForContext(value).ToString("0.####", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"\b[A-Z][A-Z0-9]*_[A-Z0-9_]+\b")]
    private static partial Regex ParameterName();
}
