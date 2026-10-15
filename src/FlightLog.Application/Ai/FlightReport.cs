using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using FlightLog.Domain.Analysis;

namespace FlightLog.Application.Ai;

public enum AiOverallAssessment
{
    NoIssuesFound = 1,
    MinorIssues = 2,
    SignificantIssues = 3,
    CriticalIssues = 4,
    Inconclusive = 5,
}

public enum AiItemKind
{
    Issue = 1,
    Observation = 2,
    DataQuality = 3,
}

/// <summary>A context item an AI report item cites, resolved by the server (the model only gives the reference).</summary>
public sealed record AiEvidenceDto(string Ref, EvidenceKind Kind, string Label, string Relevance, long? StartUs, long? EndUs, Guid? FindingId, string? Signal);

/// <summary>
/// One statement of the model. <see cref="Supported"/> is false when none of its references exists in the
/// context; such items are kept but shown apart as unsupported claims. <see cref="UnverifiedNumbers"/> lists
/// numbers in its text that do not appear in the context.
/// </summary>
public sealed record AiReportItemDto(
    int Index,
    string Title,
    AiItemKind Kind,
    string Statement,
    FindingSeverity Severity,
    FindingConfidence Confidence,
    string ConfidenceReason,
    bool Supported,
    IReadOnlyList<AiEvidenceDto> Evidence,
    IReadOnlyList<string> UnknownRefs,
    IReadOnlyList<string> UnverifiedNumbers,
    IReadOnlyList<string> PossibleCauses,
    IReadOnlyList<string> AlternativeExplanations,
    IReadOnlyList<string> MissingData,
    IReadOnlyList<string> RecommendedChecks);

/// <summary>What the server checked in the model's answer.</summary>
public sealed record AiReportValidationDto(
    int Items,
    int SupportedItems,
    int UnsupportedItems,
    IReadOnlyList<string> UnknownRefs,
    int UnverifiedNumbers,
    IReadOnlyList<string> SummaryUnverifiedNumbers);

public sealed record AiReportDto(
    string Summary,
    string PlainSummary,
    AiOverallAssessment OverallAssessment,
    IReadOnlyList<AiReportItemDto> Items,
    IReadOnlyList<string> FollowUpQuestions,
    IReadOnlyList<string> Limitations,
    AiReportValidationDto Validation);

/// <summary>The answer could not be read as a report (invalid JSON or a missing field).</summary>
public sealed class AiReportFormatException(string message) : Exception(message);

/// <summary>Reads the model's answer and checks it against the context it was given (ADR-0008).</summary>
public static partial class FlightReportValidator
{
    /// <summary>How validated reports are stored and returned.</summary>
    public static readonly JsonSerializerOptions StorageOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly JsonSerializerOptions ReadOptions = new(JsonSerializerDefaults.Web);

    public static AiReportDto Validate(string answer, FlightContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var raw = Parse(answer);
        var allUnknown = new SortedSet<string>(StringComparer.Ordinal);
        var items = new List<AiReportItemDto>();
        var index = 0;
        foreach (var item in raw.Items!)
        {
            index++;
            var evidence = new List<AiEvidenceDto>();
            var unknown = new List<string>();
            foreach (var e in item.Evidence ?? [])
            {
                var reference = NormalizeRef(e.Ref);
                if (context.Evidence.TryGetValue(reference, out var found))
                {
                    if (evidence.All(x => x.Ref != reference))
                    {
                        evidence.Add(new AiEvidenceDto(reference, found.Kind, found.Label, e.Relevance?.Trim() ?? "", found.StartUs, found.EndUs, found.FindingId, found.Signal));
                    }
                }
                else if (!string.IsNullOrEmpty(reference))
                {
                    unknown.Add(reference);
                    allUnknown.Add(reference);
                }
            }

            var unverified = UnverifiedNumbers(
                [item.Title, item.Statement, .. item.PossibleCauses ?? [], .. item.AlternativeExplanations ?? []], context.Numbers);
            items.Add(new AiReportItemDto(
                index,
                Required(item.Title, "items.title"),
                ParseEnum(item.Kind, "items.kind", KindValues),
                Required(item.Statement, "items.statement"),
                ParseEnum(item.Severity, "items.severity", SeverityValues),
                ParseEnum(item.Confidence, "items.confidence", ConfidenceValues),
                item.ConfidenceReason?.Trim() ?? "",
                evidence.Count > 0,
                evidence,
                unknown,
                unverified,
                Texts(item.PossibleCauses),
                Texts(item.AlternativeExplanations),
                Texts(item.MissingData),
                Texts(item.RecommendedChecks)));
        }

        var summary = Required(raw.Summary, "summary");
        var plain = raw.PlainSummary?.Trim() ?? "";
        var summaryUnverified = UnverifiedNumbers([summary, plain], context.Numbers);
        var supported = items.Count(i => i.Supported);
        return new AiReportDto(
            summary,
            plain,
            ParseEnum(raw.OverallAssessment, "overallAssessment", AssessmentValues),
            items,
            Texts(raw.FollowUpQuestions),
            Texts(raw.Limitations),
            new AiReportValidationDto(items.Count, supported, items.Count - supported, [.. allUnknown], items.Sum(i => i.UnverifiedNumbers.Count), summaryUnverified));
    }

    /// <summary>Numbers in the texts that match no number of the context (rounding tolerated). Integers up to 10 are not checked.</summary>
    public static IReadOnlyList<string> UnverifiedNumbers(IEnumerable<string?> texts, IReadOnlyList<double> known)
    {
        ArgumentNullException.ThrowIfNull(texts);
        ArgumentNullException.ThrowIfNull(known);
        var result = new List<string>();
        foreach (var text in texts)
        {
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            foreach (Match match in NumberPattern().Matches(text))
            {
                var token = match.Value;
                var normalized = token.Replace(',', '.');
                if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                {
                    continue;
                }

                var decimals = normalized.Contains('.', StringComparison.Ordinal) ? normalized.Length - normalized.IndexOf('.', StringComparison.Ordinal) - 1 : 0;
                if (decimals == 0 && Math.Abs(value) <= 10)
                {
                    continue;
                }

                if (!Matches(value, decimals, known) && !Matches(-value, decimals, known) && !result.Contains(token))
                {
                    result.Add(token);
                }
            }
        }

        return result;
    }

    private static bool Matches(double value, int decimals, IReadOnlyList<double> known)
    {
        // A number written with fewer decimals than the context (12.3 for 12.34) is a rounding, not a new value.
        var half = 0.5 * Math.Pow(10, -decimals);
        foreach (var n in known)
        {
            if (Math.Abs(value - n) <= Math.Max(half + 1e-9, Math.Abs(n) * 0.005))
            {
                return true;
            }
        }

        return false;
    }

    private static RawReport Parse(string answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
        {
            throw new AiReportFormatException("The answer is empty.");
        }

        RawReport? raw;
        try
        {
            raw = JsonSerializer.Deserialize<RawReport>(answer.Trim(), ReadOptions);
        }
        catch (JsonException ex)
        {
            throw new AiReportFormatException($"The answer is not valid JSON ({ex.Message}).");
        }

        if (raw is null || raw.Items is null)
        {
            throw new AiReportFormatException("The answer has no items list.");
        }

        return raw;
    }

    private static string NormalizeRef(string? reference) =>
        (reference ?? "").Trim().Trim('[', ']', '(', ')', '#').ToUpperInvariant();

    private static string Required(string? value, string field) =>
        string.IsNullOrWhiteSpace(value) ? throw new AiReportFormatException($"The field {field} is missing.") : value.Trim();

    private static IReadOnlyList<string> Texts(IReadOnlyList<string>? values) =>
        [.. (values ?? []).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim())];

    private static T ParseEnum<T>(string? value, string field, IReadOnlyDictionary<string, T> values) =>
        value is not null && values.TryGetValue(value.Trim().ToLowerInvariant(), out var parsed)
            ? parsed
            : throw new AiReportFormatException($"The field {field} has the unknown value '{value}'.");

    private static readonly Dictionary<string, AiItemKind> KindValues = new()
    {
        ["issue"] = AiItemKind.Issue,
        ["observation"] = AiItemKind.Observation,
        ["data_quality"] = AiItemKind.DataQuality,
    };

    private static readonly Dictionary<string, FindingSeverity> SeverityValues = new()
    {
        ["info"] = FindingSeverity.Info,
        ["warning"] = FindingSeverity.Warning,
        ["critical"] = FindingSeverity.Critical,
    };

    private static readonly Dictionary<string, FindingConfidence> ConfidenceValues = new()
    {
        ["low"] = FindingConfidence.Low,
        ["medium"] = FindingConfidence.Medium,
        ["high"] = FindingConfidence.High,
    };

    private static readonly Dictionary<string, AiOverallAssessment> AssessmentValues = new()
    {
        ["no_issues_found"] = AiOverallAssessment.NoIssuesFound,
        ["minor_issues"] = AiOverallAssessment.MinorIssues,
        ["significant_issues"] = AiOverallAssessment.SignificantIssues,
        ["critical_issues"] = AiOverallAssessment.CriticalIssues,
        ["inconclusive"] = AiOverallAssessment.Inconclusive,
    };

    // A number not glued to a word, reference or version string (F1, EKF2, 1.14.3 are not measurements).
    [GeneratedRegex(@"(?<![\p{L}\p{N}_.,])-?\d+(?:[.,]\d+)?(?![\p{N}]|[.,]\d|\p{L}\p{N})")]
    private static partial Regex NumberPattern();

    private sealed class RawReport
    {
        public string? Summary { get; set; }

        public string? PlainSummary { get; set; }

        public string? OverallAssessment { get; set; }

        public List<RawItem>? Items { get; set; }

        public List<string>? FollowUpQuestions { get; set; }

        public List<string>? Limitations { get; set; }
    }

    private sealed class RawItem
    {
        public string? Title { get; set; }

        public string? Kind { get; set; }

        public string? Statement { get; set; }

        public string? Severity { get; set; }

        public string? Confidence { get; set; }

        public string? ConfidenceReason { get; set; }

        public List<RawEvidence>? Evidence { get; set; }

        public List<string>? PossibleCauses { get; set; }

        public List<string>? AlternativeExplanations { get; set; }

        public List<string>? MissingData { get; set; }

        public List<string>? RecommendedChecks { get; set; }
    }

    private sealed class RawEvidence
    {
        public string? Ref { get; set; }

        public string? Relevance { get; set; }
    }
}
