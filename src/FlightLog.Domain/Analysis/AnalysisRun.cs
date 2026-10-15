using FlightLog.Domain.Common;

namespace FlightLog.Domain.Analysis;

public enum AnalysisKind
{
    /// <summary>Deterministic technical rules (no AI).</summary>
    Rules = 1,
}

public enum AnalysisTrigger
{
    /// <summary>Started by processing when a log file was processed.</summary>
    Automatic = 1,

    /// <summary>Requested by a user.</summary>
    Manual = 2,
}

public enum AnalysisRunStatus
{
    Queued = 1,
    Running = 2,
    Succeeded = 3,
    Failed = 4,
}

/// <summary>A run's finding counts by severity, kept on the run so that lists do not have to count findings.</summary>
public sealed record AnalysisCounts(int Info, int Warning, int Critical, int RulesApplied, int RulesNotApplicable, int RulesFailed)
{
    public int Total => Info + Warning + Critical;
}

/// <summary>Raised when a run enters the queue; <see cref="NotBefore"/> delays an automatic retry.</summary>
public sealed record AnalysisRunQueued(Guid AnalysisRunId, Guid FlightId, Guid TeamId, DateTimeOffset? NotBefore) : IDomainEvent;

/// <summary>
/// One execution of the analysis rules on a flight's processed data. Runs are history: a new run never changes
/// the findings of an earlier one, so results stay reproducible together with the engine version they record.
/// </summary>
public sealed class AnalysisRun : IHasDomainEvents
{
    public const int FailureMessageMaxLength = 1000;

    private readonly List<IDomainEvent> _domainEvents = [];

    private AnalysisRun()
    {
        EngineVersion = null!;
        CorrelationId = null!;
    }

    public Guid Id { get; private set; }

    public Guid TeamId { get; private set; }

    public Guid FlightId { get; private set; }

    public Guid LogFileId { get; private set; }

    public AnalysisKind Kind { get; private set; }

    public AnalysisTrigger Trigger { get; private set; }

    /// <summary>Null for automatic runs.</summary>
    public Guid? RequestedBy { get; private set; }

    public AnalysisRunStatus Status { get; private set; }

    /// <summary>Version of the rule set; set when the run starts.</summary>
    public string EngineVersion { get; private set; }

    public string CorrelationId { get; private set; }

    /// <summary>Times the run was started (more than one after a transient failure or a worker shutdown).</summary>
    public int Attempt { get; private set; }

    /// <summary>Automatic retries after transient failures.</summary>
    public int RetryCount { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>A queued run waiting for an automatic retry is not picked up before this time.</summary>
    public DateTimeOffset? NextAttemptAt { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? FinishedAt { get; private set; }

    public long? DurationMs { get; private set; }

    public string? WorkerId { get; private set; }

    /// <summary>What the run looked at: processing job, normalization version, analyzed time range...</summary>
    public IReadOnlyDictionary<string, string> InputSummary { get; private set; } = new Dictionary<string, string>();

    public int FindingCount { get; private set; }

    public int InfoCount { get; private set; }

    public int WarningCount { get; private set; }

    public int CriticalCount { get; private set; }

    public int RulesApplied { get; private set; }

    public int RulesNotApplicable { get; private set; }

    public int RulesFailed { get; private set; }

    public string? FailureCode { get; private set; }

    public string? FailureMessage { get; private set; }

    public uint Version { get; private set; }

    public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents;

    public void ClearDomainEvents() => _domainEvents.Clear();

    public static AnalysisRun Queue(Guid teamId, Guid flightId, Guid logFileId, AnalysisTrigger trigger, Guid? requestedBy, DateTimeOffset now)
    {
        if (trigger == AnalysisTrigger.Manual && requestedBy is null)
        {
            throw new DomainRuleViolationException("analysis_run.requester_required", "A manual run needs the requesting user.");
        }

        var run = new AnalysisRun
        {
            Id = Guid.CreateVersion7(now),
            TeamId = Guard.NotEmpty(teamId, "teamId"),
            FlightId = Guard.NotEmpty(flightId, "flightId"),
            LogFileId = Guard.NotEmpty(logFileId, "logFileId"),
            Kind = AnalysisKind.Rules,
            Trigger = trigger,
            RequestedBy = requestedBy,
            Status = AnalysisRunStatus.Queued,
            EngineVersion = "",
            CorrelationId = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
        };
        run._domainEvents.Add(new AnalysisRunQueued(run.Id, flightId, teamId, null));
        return run;
    }

    public void Start(string workerId, string engineVersion, DateTimeOffset now)
    {
        EnsureStatus(AnalysisRunStatus.Queued);
        Status = AnalysisRunStatus.Running;
        WorkerId = Guard.RequiredText(workerId, 200, "workerId");
        EngineVersion = Guard.RequiredText(engineVersion, 50, "engineVersion");
        Attempt++;
        StartedAt = now;
        NextAttemptAt = null;
    }

    public void Succeed(AnalysisCounts counts, IReadOnlyDictionary<string, string> inputSummary, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(counts);
        ArgumentNullException.ThrowIfNull(inputSummary);
        Finish(AnalysisRunStatus.Succeeded, now);
        InfoCount = counts.Info;
        WarningCount = counts.Warning;
        CriticalCount = counts.Critical;
        FindingCount = counts.Total;
        RulesApplied = counts.RulesApplied;
        RulesNotApplicable = counts.RulesNotApplicable;
        RulesFailed = counts.RulesFailed;
        InputSummary = new Dictionary<string, string>(inputSummary, StringComparer.Ordinal);
    }

    /// <summary>Ends the run without result. Also allowed for a queued run (for example its file was reset).</summary>
    public void Fail(string code, string message, DateTimeOffset now)
    {
        if (Status is not (AnalysisRunStatus.Queued or AnalysisRunStatus.Running))
        {
            throw new DomainRuleViolationException("analysis_run.already_finished", $"The run already ended as {Status}.");
        }

        Finish(AnalysisRunStatus.Failed, now);
        FailureCode = Guard.RequiredText(code, 100, "failureCode");
        var text = Guard.RequiredText(message, int.MaxValue, "failureMessage");
        FailureMessage = text.Length > FailureMessageMaxLength ? text[..FailureMessageMaxLength] : text;
    }

    /// <summary>Puts a running run back into the queue: retry after a transient failure, or worker shutdown.</summary>
    public void Requeue(DateTimeOffset? notBefore, bool countsAsRetry)
    {
        EnsureStatus(AnalysisRunStatus.Running);
        Status = AnalysisRunStatus.Queued;
        StartedAt = null;
        NextAttemptAt = notBefore;
        if (countsAsRetry)
        {
            RetryCount++;
        }

        _domainEvents.Add(new AnalysisRunQueued(Id, FlightId, TeamId, notBefore));
    }

    private void Finish(AnalysisRunStatus status, DateTimeOffset now)
    {
        Status = status;
        FinishedAt = now;
        DurationMs = StartedAt is { } started ? (long)Math.Max(0, (now - started).TotalMilliseconds) : null;
    }

    private void EnsureStatus(AnalysisRunStatus expected)
    {
        if (Status != expected)
        {
            throw new DomainRuleViolationException("analysis_run.invalid_status", $"The run is {Status}, expected {expected}.");
        }
    }
}
