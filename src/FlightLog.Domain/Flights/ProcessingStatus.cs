using FlightLog.Domain.Common;

namespace FlightLog.Domain.Flights;

/// <summary>Lifecycle of an uploaded log file, from upload to completed analysis.</summary>
public enum ProcessingStatus
{
    Uploaded = 1,
    Validating = 2,
    Queued = 3,
    Processing = 4,
    Processed = 5,
    AnalysisPending = 6,
    Analyzing = 7,
    Completed = 8,
    Failed = 9,
    Cancelled = 10,
}

/// <summary>The allowed transitions of <see cref="ProcessingStatus"/>; everything else is rejected.</summary>
public static class ProcessingStatusTransitions
{
    private static readonly Dictionary<ProcessingStatus, ProcessingStatus[]> Allowed = new()
    {
        [ProcessingStatus.Uploaded] = [ProcessingStatus.Validating],
        [ProcessingStatus.Validating] = [ProcessingStatus.Queued, ProcessingStatus.Failed],
        [ProcessingStatus.Queued] = [ProcessingStatus.Processing, ProcessingStatus.Cancelled],
        [ProcessingStatus.Processing] = [ProcessingStatus.Processed, ProcessingStatus.Failed, ProcessingStatus.Cancelled],
        [ProcessingStatus.Processed] = [ProcessingStatus.AnalysisPending],
        [ProcessingStatus.AnalysisPending] = [ProcessingStatus.Analyzing, ProcessingStatus.Cancelled],
        [ProcessingStatus.Analyzing] = [ProcessingStatus.Completed, ProcessingStatus.Failed],
        [ProcessingStatus.Completed] = [ProcessingStatus.AnalysisPending],
        [ProcessingStatus.Failed] = [ProcessingStatus.Queued],
        [ProcessingStatus.Cancelled] = [ProcessingStatus.Queued],
    };

    public static bool CanTransition(ProcessingStatus from, ProcessingStatus to) =>
        Allowed.TryGetValue(from, out var targets) && targets.Contains(to);

    public static void EnsureCanTransition(ProcessingStatus from, ProcessingStatus to)
    {
        if (!CanTransition(from, to))
        {
            throw new DomainRuleViolationException(
                "log_file.invalid_status_transition",
                $"A log file cannot move from {from} to {to}.");
        }
    }

    public static bool IsTerminal(ProcessingStatus status) =>
        status is ProcessingStatus.Completed or ProcessingStatus.Failed or ProcessingStatus.Cancelled;
}
