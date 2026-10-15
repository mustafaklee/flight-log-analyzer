using FlightLog.Domain.Common;

namespace FlightLog.Domain.Flights;

/// <summary>
/// The raw log file of a flight. The bytes live in object storage under <see cref="StorageKey"/>;
/// this entity tracks identity (SHA-256), format and processing state.
/// </summary>
public sealed class FlightLogFile : IHasDomainEvents
{
    public const int FileNameMaxLength = 255;
    public const int FailureMessageMaxLength = 1000;

    /// <summary>Failure codes of the analysis step start with this; the processed data is intact after them.</summary>
    public const string AnalysisFailurePrefix = "analysis.";

    private readonly List<IDomainEvent> _domainEvents = [];

    private FlightLogFile()
    {
        OriginalFileName = null!;
        Sha256 = null!;
        StorageKey = null!;
    }

    private FlightLogFile(Guid id, Guid teamId, Guid flightId, string originalFileName, long sizeBytes, string sha256, LogFormat format, string storageKey, Guid uploadedBy, DateTimeOffset now)
    {
        Id = id;
        TeamId = teamId;
        FlightId = flightId;
        OriginalFileName = originalFileName;
        SizeBytes = sizeBytes;
        Sha256 = sha256;
        Format = format;
        StorageKey = storageKey;
        UploadedBy = uploadedBy;
        UploadedAt = now;
        Status = ProcessingStatus.Uploaded;
        StatusChangedAt = now;
    }

    public Guid Id { get; private set; }

    public Guid TeamId { get; private set; }

    public Guid FlightId { get; private set; }

    /// <summary>Display-only name as supplied by the uploader (sanitized); never used as a storage path.</summary>
    public string OriginalFileName { get; private set; }

    public long SizeBytes { get; private set; }

    /// <summary>Lower-case hex SHA-256 of the file content.</summary>
    public string Sha256 { get; private set; }

    public LogFormat Format { get; private set; }

    public string StorageKey { get; private set; }

    public Guid UploadedBy { get; private set; }

    public DateTimeOffset UploadedAt { get; private set; }

    public ProcessingStatus Status { get; private set; }

    public DateTimeOffset StatusChangedAt { get; private set; }

    /// <summary>Stable error code of the last failure, safe to show to users.</summary>
    public string? FailureCode { get; private set; }

    public string? FailureMessage { get; private set; }

    /// <summary>Automatic retries after transient failures since the file was last queued by a user.</summary>
    public int RetryCount { get; private set; }

    /// <summary>A queued file waiting for an automatic retry is not picked up before this time.</summary>
    public DateTimeOffset? NextAttemptAt { get; private set; }

    public uint Version { get; private set; }

    public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents;

    public void ClearDomainEvents() => _domainEvents.Clear();

    public static FlightLogFile Create(Guid teamId, Guid flightId, string originalFileName, long sizeBytes, string sha256, LogFormat format, string storageKey, Guid uploadedBy, DateTimeOffset now)
    {
        if (sizeBytes <= 0)
        {
            throw new DomainRuleViolationException("log_file.empty", "The log file is empty.");
        }

        if (sha256 is null || sha256.Length != 64 || !sha256.All(char.IsAsciiHexDigitLower))
        {
            throw new DomainRuleViolationException("log_file.invalid_hash", "SHA-256 must be 64 lower-case hex characters.");
        }

        if (!Enum.IsDefined(format))
        {
            throw new DomainRuleViolationException("log_file.invalid_format", "Unknown log format.");
        }

        return new FlightLogFile(
            Guid.CreateVersion7(now),
            Guard.NotEmpty(teamId, "teamId"),
            Guard.NotEmpty(flightId, "flightId"),
            Guard.RequiredText(originalFileName, FileNameMaxLength, "fileName"),
            sizeBytes,
            sha256,
            format,
            Guard.RequiredText(storageKey, 500, "storageKey"),
            Guard.NotEmpty(uploadedBy, "uploadedBy"),
            now);
    }

    public void TransitionTo(ProcessingStatus target, DateTimeOffset now)
    {
        if (target == ProcessingStatus.Failed)
        {
            throw new DomainRuleViolationException("log_file.failure_requires_reason", "Use Fail to record a failure with its reason.");
        }

        ProcessingStatusTransitions.EnsureCanTransition(Status, target);
        Status = target;
        StatusChangedAt = now;
        if (target == ProcessingStatus.Queued)
        {
            FailureCode = null;
            FailureMessage = null;
            NextAttemptAt = null;
            _domainEvents.Add(new LogFileQueued(Id, TeamId, null));
        }
    }

    /// <summary>
    /// Puts a file whose processing failed for a transient reason (storage or database unavailable, worker
    /// crash) back into the queue, to be picked up again not before <paramref name="notBefore"/>.
    /// </summary>
    public void ScheduleRetry(DateTimeOffset notBefore, DateTimeOffset now)
    {
        if (Status != ProcessingStatus.Processing)
        {
            throw new DomainRuleViolationException("log_file.not_processing", $"Only a file in processing can be retried automatically (current status: {Status}).");
        }

        TransitionTo(ProcessingStatus.Queued, now);
        RetryCount++;
        NextAttemptAt = notBefore;
        _domainEvents.RemoveAll(e => e is LogFileQueued);
        _domainEvents.Add(new LogFileQueued(Id, TeamId, notBefore));
    }

    public void Fail(string code, string message, DateTimeOffset now)
    {
        ProcessingStatusTransitions.EnsureCanTransition(Status, ProcessingStatus.Failed);
        Status = ProcessingStatus.Failed;
        StatusChangedAt = now;
        FailureCode = Guard.RequiredText(code, 100, "failureCode");
        var text = Guard.RequiredText(message, int.MaxValue, "failureMessage");
        FailureMessage = text.Length > FailureMessageMaxLength ? text[..FailureMessageMaxLength] : text;
    }

    /// <summary>Puts a failed or cancelled file back into the processing queue.</summary>
    public void Retry(DateTimeOffset now)
    {
        if (Status is not (ProcessingStatus.Failed or ProcessingStatus.Cancelled))
        {
            throw new DomainRuleViolationException("log_file.not_retryable", $"Only failed or cancelled files can be retried (current status: {Status}).");
        }

        TransitionTo(ProcessingStatus.Queued, now);
        RetryCount = 0;
    }

    /// <summary>True when the file's processed data can be analyzed (again).</summary>
    public bool CanRequestAnalysis =>
        Status is ProcessingStatus.Processed or ProcessingStatus.Completed
        || (Status == ProcessingStatus.Failed && FailureCode?.StartsWith(AnalysisFailurePrefix, StringComparison.Ordinal) == true);

    /// <summary>Marks the file as waiting for an analysis run.</summary>
    public void RequestAnalysis(DateTimeOffset now)
    {
        if (!CanRequestAnalysis)
        {
            throw new DomainRuleViolationException("log_file.not_analyzable", $"A file in status {Status} cannot be analyzed. Process it first.");
        }

        TransitionTo(ProcessingStatus.AnalysisPending, now);
        FailureCode = null;
        FailureMessage = null;
    }

    public void Cancel(DateTimeOffset now)
    {
        if (!ProcessingStatusTransitions.CanTransition(Status, ProcessingStatus.Cancelled))
        {
            throw new DomainRuleViolationException("log_file.not_cancellable", $"A file in status {Status} cannot be cancelled.");
        }

        TransitionTo(ProcessingStatus.Cancelled, now);
    }
}
