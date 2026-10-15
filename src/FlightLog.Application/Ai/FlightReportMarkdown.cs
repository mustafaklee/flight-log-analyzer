using System.Globalization;
using System.Text;
using FlightLog.Domain.Analysis;

namespace FlightLog.Application.Ai;

/// <summary>Renders a stored AI report as Markdown for download (Turkish, like the UI).</summary>
public static class FlightReportMarkdown
{
    public static string Render(AiAnalysisDto analysis, string flightName)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        var report = analysis.Report ?? throw new InvalidOperationException("The analysis has no report.");
        var md = new StringBuilder();
        md.Append("# AI analiz raporu: ").AppendLine(Escape(flightName)).AppendLine();
        md.AppendLine("> Bu rapor bir dil modelinin yorumudur; kural bulgularının yerine geçmez. Her madde, sunucunun doğruladığı kanıt referanslarıyla verilir.");
        md.AppendLine();
        md.AppendLine("| | |").AppendLine("|---|---|");
        Row(md, "Model", $"{analysis.Provider} {analysis.ResponseModel ?? analysis.Model}");
        Row(md, "Prompt sürümü", analysis.PromptVersion);
        Row(md, "Bağlam", $"{analysis.ContextVersion} (SHA-256 {analysis.ContextHash?[..12]}…)");
        Row(md, "Kural analizi", analysis.AnalysisRunId.ToString());
        Row(md, "Oluşturma", analysis.FinishedAt?.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) ?? "-");
        Row(md, "Token (girdi / çıktı)", $"{analysis.InputTokens} / {analysis.OutputTokens}");
        Row(md, "Tahmini maliyet", analysis.CostUsd is { } c ? c.ToString("0.0000 'USD'", CultureInfo.InvariantCulture) : "fiyat tanımlı değil");
        md.AppendLine();

        md.AppendLine("## Özet").AppendLine().AppendLine(Escape(report.Summary)).AppendLine();
        if (!string.IsNullOrWhiteSpace(report.PlainSummary))
        {
            md.AppendLine("**Sade anlatım:** ").Append(Escape(report.PlainSummary)).AppendLine().AppendLine();
        }

        md.Append("**Genel değerlendirme:** ").AppendLine(Assessment(report.OverallAssessment)).AppendLine();
        var v = report.Validation;
        md.Append(CultureInfo.InvariantCulture, $"**Doğrulama:** {v.Items} madde, {v.SupportedItems} kanıtla destekli, {v.UnsupportedItems} desteklenmeyen");
        md.AppendLine(CultureInfo.InvariantCulture, $"; bağlamda bulunmayan {v.UnverifiedNumbers + v.SummaryUnverifiedNumbers.Count} sayı işaretlendi.").AppendLine();

        var supported = report.Items.Where(i => i.Supported).ToList();
        md.AppendLine("## Bulgular").AppendLine();
        if (supported.Count == 0)
        {
            md.AppendLine("Kanıtla desteklenen madde yok.").AppendLine();
        }

        foreach (var item in supported)
        {
            Item(md, item, analysis);
        }

        var unsupported = report.Items.Where(i => !i.Supported).ToList();
        if (unsupported.Count > 0)
        {
            md.AppendLine("## Desteklenmeyen iddialar").AppendLine();
            md.AppendLine("Model bu maddeler için bağlamda var olan bir kanıt göstermedi. Doğrulanmamış kabul edin.").AppendLine();
            foreach (var item in unsupported)
            {
                Item(md, item, analysis);
            }
        }

        List(md, "Açık sorular", report.FollowUpQuestions);
        List(md, "Sınırlamalar", report.Limitations);
        return md.ToString();
    }

    private static void Item(StringBuilder md, AiReportItemDto item, AiAnalysisDto analysis)
    {
        md.Append(CultureInfo.InvariantCulture, $"### {item.Index}. {Escape(item.Title)}").AppendLine().AppendLine();
        md.Append(CultureInfo.InvariantCulture, $"*{Kind(item.Kind)} · önem: {Severity(item.Severity)} · güven: {Confidence(item.Confidence)}*").AppendLine().AppendLine();
        md.AppendLine(Escape(item.Statement)).AppendLine();
        if (!string.IsNullOrWhiteSpace(item.ConfidenceReason))
        {
            md.Append("Güven gerekçesi: ").AppendLine(Escape(item.ConfidenceReason)).AppendLine();
        }

        if (item.Evidence.Count > 0)
        {
            md.AppendLine("Kanıt:");
            foreach (var e in item.Evidence)
            {
                var time = e.StartUs is { } s ? $" ({Time(s, analysis)}{(e.EndUs is { } end && end != s ? $"–{Time(end, analysis)}" : "")})" : "";
                md.Append(CultureInfo.InvariantCulture, $"- **{e.Ref}** {Escape(e.Label)}{time}: {Escape(e.Relevance)}").AppendLine();
            }

            md.AppendLine();
        }

        if (item.UnknownRefs.Count > 0)
        {
            md.Append("Bağlamda bulunmayan referanslar (yok sayıldı): ").AppendLine(string.Join(", ", item.UnknownRefs)).AppendLine();
        }

        if (item.UnverifiedNumbers.Count > 0)
        {
            md.Append("Bağlamda bulunmayan sayılar: ").AppendLine(string.Join(", ", item.UnverifiedNumbers)).AppendLine();
        }

        SubList(md, "Olası nedenler", item.PossibleCauses);
        SubList(md, "Alternatif açıklamalar", item.AlternativeExplanations);
        SubList(md, "Eksik veri", item.MissingData);
        SubList(md, "Önerilen kontroller", item.RecommendedChecks);
    }

    private static string Time(long us, AiAnalysisDto analysis) =>
        analysis.LogStartUs is { } start ? $"t={((us - start) / 1e6).ToString("0.0", CultureInfo.InvariantCulture)} s" : $"{us} µs";

    private static void Row(StringBuilder md, string key, string value) => md.Append("| ").Append(key).Append(" | ").Append(Escape(value).Replace("|", "\\|", StringComparison.Ordinal)).AppendLine(" |");

    private static void List(StringBuilder md, string title, IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        md.Append("## ").AppendLine(title).AppendLine();
        foreach (var v in values)
        {
            md.Append("- ").AppendLine(Escape(v));
        }

        md.AppendLine();
    }

    private static void SubList(StringBuilder md, string title, IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        md.Append(title).AppendLine(":");
        foreach (var v in values)
        {
            md.Append("- ").AppendLine(Escape(v));
        }

        md.AppendLine();
    }

    /// <summary>Model text is not trusted as Markdown: HTML and link syntax are neutralized.</summary>
    private static string Escape(string text) => text
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("[", "\\[", StringComparison.Ordinal)
        .Replace("]", "\\]", StringComparison.Ordinal)
        .Replace("\r", "", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal);

    private static string Assessment(AiOverallAssessment a) => a switch
    {
        AiOverallAssessment.NoIssuesFound => "Sorun bulunmadı",
        AiOverallAssessment.MinorIssues => "Küçük sorunlar",
        AiOverallAssessment.SignificantIssues => "Önemli sorunlar",
        AiOverallAssessment.CriticalIssues => "Kritik sorunlar",
        _ => "Belirsiz",
    };

    private static string Kind(AiItemKind k) => k switch
    {
        AiItemKind.Issue => "Sorun",
        AiItemKind.Observation => "Gözlem",
        _ => "Veri kalitesi",
    };

    private static string Severity(FindingSeverity s) => s switch
    {
        FindingSeverity.Critical => "kritik",
        FindingSeverity.Warning => "uyarı",
        _ => "bilgi",
    };

    private static string Confidence(FindingConfidence c) => c switch
    {
        FindingConfidence.High => "yüksek",
        FindingConfidence.Medium => "orta",
        _ => "düşük",
    };
}
