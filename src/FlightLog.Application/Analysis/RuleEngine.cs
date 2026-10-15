using System.Diagnostics;
using FlightLog.Application.Abstractions;
using FlightLog.Domain.Analysis;
using Microsoft.Extensions.Logging;

namespace FlightLog.Application.Analysis;

public sealed record RuleEngineResult(IReadOnlyList<AnalysisFinding> Findings, IReadOnlyList<AnalysisRuleResult> RuleResults, AnalysisCounts Counts);

/// <summary>
/// Runs every rule on a flight. A rule that throws is recorded as failed and does not stop the others; only
/// transient infrastructure failures (signal storage unreachable) abort the run so that it can be retried.
/// </summary>
public sealed partial class RuleEngine(IEnumerable<IAnalysisRule> rules, ITransientErrorClassifier errors, ILogger<RuleEngine> logger)
{
    /// <summary>Version of the rule set as a whole; each finding also records its rule's own version.</summary>
    public const string Version = "rules-1";

    /// <summary>Protects storage and UI from a rule that matches on every sample of a noisy log.</summary>
    public const int MaxFindingsPerRule = 200;

    private readonly IReadOnlyList<IAnalysisRule> _rules = [.. rules.OrderBy(r => r.Descriptor.Id, StringComparer.Ordinal)];

    public IReadOnlyList<RuleDescriptor> Rules => [.. _rules.Select(r => r.Descriptor)];

    public async Task<RuleEngineResult> RunAsync(AnalysisContext context, Guid runId, Guid flightId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var findings = new List<AnalysisFinding>();
        var results = new List<AnalysisRuleResult>();
        foreach (var rule in _rules)
        {
            var d = rule.Descriptor;
            var watch = Stopwatch.StartNew();
            RuleEvaluation evaluation;
            try
            {
                evaluation = await rule.EvaluateAsync(context, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && !errors.IsTransient(ex))
            {
                LogRuleFailed(ex, d.Id);
                evaluation = new RuleEvaluation(RuleResultStatus.Failed, $"The rule failed with an internal error ({ex.GetType().Name}).", [], []);
            }

            var kept = evaluation.Findings.OrderBy(f => f.StartUs).Take(MaxFindingsPerRule).ToList();
            var reason = evaluation.Findings.Count > MaxFindingsPerRule
                ? $"Only the first {MaxFindingsPerRule} of {evaluation.Findings.Count} findings were kept."
                : evaluation.Reason;
            findings.AddRange(kept.Select(f => ToFinding(f, d, runId, flightId)));
            results.Add(new AnalysisRuleResult
            {
                RunId = runId,
                RuleId = d.Id,
                RuleVersion = d.Version,
                Status = evaluation.Status,
                Reason = reason,
                FindingCount = kept.Count,
                Signals = evaluation.Signals,
                DurationMs = watch.ElapsedMilliseconds,
            });
        }

        var counts = new AnalysisCounts(
            findings.Count(f => f.Severity == FindingSeverity.Info),
            findings.Count(f => f.Severity == FindingSeverity.Warning),
            findings.Count(f => f.Severity == FindingSeverity.Critical),
            results.Count(r => r.Status == RuleResultStatus.Applied),
            results.Count(r => r.Status == RuleResultStatus.NotApplicable),
            results.Count(r => r.Status == RuleResultStatus.Failed));
        return new RuleEngineResult(findings, results, counts);
    }

    private static AnalysisFinding ToFinding(FindingDraft f, RuleDescriptor rule, Guid runId, Guid flightId) => new()
    {
        Id = Guid.CreateVersion7(),
        RunId = runId,
        FlightId = flightId,
        RuleId = rule.Id,
        RuleVersion = rule.Version,
        Category = rule.Category,
        Title = Shorten(f.Title, AnalysisFinding.TitleMaxLength),
        Description = Shorten(f.Description, AnalysisFinding.DescriptionMaxLength),
        Severity = f.Severity,
        Confidence = f.Confidence,
        StartUs = f.StartUs,
        EndUs = Math.Max(f.StartUs, f.EndUs),
        Signals = [.. f.Signals.Take(50)],
        Measured = f.Measured.Where(m => double.IsFinite(m.Value)).Take(50).ToDictionary(m => m.Key, m => m.Value, StringComparer.Ordinal),
        Threshold = f.Threshold.Where(m => double.IsFinite(m.Value)).ToDictionary(m => m.Key, m => m.Value, StringComparer.Ordinal),
        ThresholdSource = Shorten(f.ThresholdSource, 500),
        DataCoverage = f.DataCoverage is { } c ? Math.Round(c, 4) : null,
        OccurrenceCount = Math.Max(1, f.OccurrenceCount),
        Limitations = f.Limitations is null ? null : Shorten(f.Limitations, 1000),
    };

    private static string Shorten(string value, int max) => value.Length > max ? value[..max] : value;

    [LoggerMessage(Level = LogLevel.Error, Message = "Analysis rule {RuleId} failed")]
    private partial void LogRuleFailed(Exception exception, string ruleId);
}
