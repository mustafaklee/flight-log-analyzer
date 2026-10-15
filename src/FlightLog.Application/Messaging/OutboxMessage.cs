namespace FlightLog.Application.Messaging;

/// <summary>
/// A message written in the same transaction as the change it announces, and forwarded to the broker by the
/// outbox publisher. Rows are never sent before <see cref="AvailableAt"/> (delayed retries).
/// </summary>
public sealed class OutboxMessage
{
    public const int ErrorMaxLength = 1000;

    private OutboxMessage()
    {
        Type = null!;
        Payload = null!;
        CorrelationId = null!;
    }

    public Guid Id { get; private set; }

    /// <summary>Message type (see <c>MessageTypes</c>), also the broker message's type property.</summary>
    public string Type { get; private set; }

    /// <summary>The log file or analysis run the message is about.</summary>
    public Guid AggregateId { get; private set; }

    /// <summary>JSON payload.</summary>
    public string Payload { get; private set; }

    public string CorrelationId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset AvailableAt { get; private set; }

    public DateTimeOffset? PublishedAt { get; private set; }

    public int PublishAttempts { get; private set; }

    public string? LastError { get; private set; }

    public static OutboxMessage Create(string type, Guid aggregateId, string payload, string correlationId, DateTimeOffset now, DateTimeOffset? availableAt) => new()
    {
        Id = Guid.CreateVersion7(now),
        Type = type,
        AggregateId = aggregateId,
        Payload = payload,
        CorrelationId = correlationId,
        CreatedAt = now,
        AvailableAt = availableAt is { } at && at > now ? at : now,
    };

    public void MarkPublished(DateTimeOffset now)
    {
        PublishedAt = now;
        PublishAttempts++;
        LastError = null;
    }

    public void MarkFailed(string error)
    {
        PublishAttempts++;
        LastError = error.Length > ErrorMaxLength ? error[..ErrorMaxLength] : error;
    }
}
