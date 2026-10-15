using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace FlightLog.Application.Processing;

/// <summary>How a piece of queued work (processing a file or running an analysis) ended.</summary>
public enum WorkOutcome
{
    Succeeded = 1,

    /// <summary>Permanent failure (corrupt file, internal error); recorded and not retried.</summary>
    Failed = 2,

    /// <summary>The user cancelled the file.</summary>
    Cancelled = 3,

    /// <summary>Nothing to do: the work was already done, claimed by another worker or is no longer wanted.</summary>
    Skipped = 4,

    /// <summary>Transient failure; queued again with a delay.</summary>
    RetryScheduled = 5,

    /// <summary>Transient failure, but the retries are used up; recorded as failed.</summary>
    RetriesExhausted = 6,

    /// <summary>The worker shut down; the work went back to the queue unchanged.</summary>
    Requeued = 7,
}

/// <summary>
/// Processing and analysis measurements (System.Diagnostics.Metrics, meter <see cref="MeterName"/>). Any
/// metrics exporter can collect them; the admin statistics endpoint gives the same figures from the database.
/// </summary>
public sealed class ProcessingMetrics
{
    public const string MeterName = "FlightLog.Processing";

    private readonly Counter<long> _jobs;
    private readonly Histogram<double> _duration;
    private readonly Histogram<double> _queueWait;
    private readonly Histogram<long> _allocated;
    private readonly Counter<long> _retries;
    private readonly Counter<long> _analysisRuns;
    private readonly Histogram<double> _analysisDuration;
    private readonly Counter<long> _findings;
    private readonly Counter<long> _aiAnalyses;
    private readonly Histogram<double> _aiDuration;
    private readonly Counter<long> _aiTokens;
    private readonly Counter<double> _aiCost;

    public ProcessingMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        var meter = meterFactory.Create(MeterName);
        _jobs = meter.CreateCounter<long>("flightlog.processing.jobs", description: "Processing attempts by outcome");
        _duration = meter.CreateHistogram<double>("flightlog.processing.duration", "s", "Duration of a processing attempt");
        _queueWait = meter.CreateHistogram<double>("flightlog.processing.queue_wait", "s", "Time a file waited in the queue before a worker took it");
        _allocated = meter.CreateHistogram<long>("flightlog.processing.allocated", "By", "Managed memory allocated while a file was processed");
        _retries = meter.CreateCounter<long>("flightlog.processing.retries", description: "Automatic retries scheduled after transient failures");
        _analysisRuns = meter.CreateCounter<long>("flightlog.analysis.runs", description: "Analysis runs by outcome");
        _analysisDuration = meter.CreateHistogram<double>("flightlog.analysis.duration", "s", "Duration of an analysis run");
        _findings = meter.CreateCounter<long>("flightlog.analysis.findings", description: "Findings by rule and severity");
        _aiAnalyses = meter.CreateCounter<long>("flightlog.ai.analyses", description: "AI analysis attempts by outcome and failure code");
        _aiDuration = meter.CreateHistogram<double>("flightlog.ai.duration", "s", "Duration of an AI analysis attempt");
        _aiTokens = meter.CreateCounter<long>("flightlog.ai.tokens", description: "Tokens used by AI analyses, by model and direction");
        _aiCost = meter.CreateCounter<double>("flightlog.ai.cost", "USD", "Estimated cost of AI analyses by model");
    }

    public void JobStarted(TimeSpan queueWait) => _queueWait.Record(Math.Max(0, queueWait.TotalSeconds));

    public void JobEnded(WorkOutcome outcome, string format, string? failureCode, TimeSpan duration, long allocatedBytes)
    {
        var tags = new TagList { { "outcome", outcome.ToString() }, { "format", format }, { "code", failureCode ?? "" } };
        _jobs.Add(1, tags);
        _duration.Record(duration.TotalSeconds, new TagList { { "outcome", outcome.ToString() }, { "format", format } });
        _allocated.Record(allocatedBytes);
        if (outcome == WorkOutcome.RetryScheduled)
        {
            _retries.Add(1, new TagList { { "stage", "processing" } });
        }
    }

    public void AnalysisEnded(WorkOutcome outcome, TimeSpan duration)
    {
        _analysisRuns.Add(1, new TagList { { "outcome", outcome.ToString() } });
        _analysisDuration.Record(duration.TotalSeconds);
        if (outcome == WorkOutcome.RetryScheduled)
        {
            _retries.Add(1, new TagList { { "stage", "analysis" } });
        }
    }

    public void Finding(string ruleId, string severity) => _findings.Add(1, new TagList { { "rule", ruleId }, { "severity", severity } });

    public void AiAnalysisEnded(WorkOutcome outcome, string? code, TimeSpan duration)
    {
        _aiAnalyses.Add(1, new TagList { { "outcome", outcome.ToString() }, { "code", code ?? "" } });
        _aiDuration.Record(duration.TotalSeconds);
        if (outcome == WorkOutcome.RetryScheduled)
        {
            _retries.Add(1, new TagList { { "stage", "ai" } });
        }
    }

    public void AiCall(string model, Domain.Analysis.AiUsage usage, decimal? cost)
    {
        ArgumentNullException.ThrowIfNull(usage);
        _aiTokens.Add(usage.InputTokens, new TagList { { "model", model }, { "direction", "input" } });
        _aiTokens.Add(usage.OutputTokens, new TagList { { "model", model }, { "direction", "output" } });
        if (cost is { } c)
        {
            _aiCost.Add((double)c, new TagList { { "model", model } });
        }
    }
}
