namespace FlightLog.Domain.Common;

/// <summary>Something that happened to an entity and that other parts of the system react to.</summary>
public interface IDomainEvent;

/// <summary>
/// An entity that records domain events. They are turned into outbox messages in the same transaction as the
/// change that raised them (see the outbox in Infrastructure), so a change and its message are never split.
/// </summary>
public interface IHasDomainEvents
{
    IReadOnlyList<IDomainEvent> DomainEvents { get; }

    void ClearDomainEvents();
}
