namespace FlightLog.Application.Abstractions;

/// <summary>Message counts of the broker queues, for the processing statistics.</summary>
public sealed record QueueDepth(string Queue, long Messages, long Consumers);

public interface IMessageQueueMonitor
{
    /// <summary>Current depth of the work queue and the dead-letter queue; empty when no broker is used or reachable.</summary>
    Task<IReadOnlyList<QueueDepth>> GetDepthsAsync(CancellationToken cancellationToken);
}
