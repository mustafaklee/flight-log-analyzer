namespace FlightLog.Application.Messaging;

public enum MessageTransport
{
    /// <summary>Workers poll PostgreSQL for queued work. For a single machine and for tests; no broker needed.</summary>
    Database = 1,

    /// <summary>Work is announced through RabbitMQ via the transactional outbox (ADR-0006).</summary>
    RabbitMq = 2,
}

public sealed class MessagingOptions
{
    public const string SectionName = "Messaging";

    public MessageTransport Transport { get; set; } = MessageTransport.Database;

    /// <summary>
    /// Safety net for lost messages: work that has been queued this long without a new message is announced again.
    /// Duplicates are harmless (claiming is idempotent), so this only needs to be longer than a normal queue wait.
    /// </summary>
    public TimeSpan OrphanAfter { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Published outbox rows are deleted after this long.</summary>
    public TimeSpan OutboxRetention { get; set; } = TimeSpan.FromDays(7);
}
