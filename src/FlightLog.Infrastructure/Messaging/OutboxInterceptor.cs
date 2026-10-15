using FlightLog.Application.Messaging;
using FlightLog.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;

namespace FlightLog.Infrastructure.Messaging;

/// <summary>
/// Turns the domain events of changed entities into outbox rows just before SaveChanges, so that they are written
/// in the same transaction as the change (ADR-0006). With the database transport no messages are needed: the
/// events are only cleared.
/// </summary>
internal sealed class OutboxInterceptor(IOptions<MessagingOptions> options, TimeProvider clock) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Collect(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Collect(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Collect(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var sources = context.ChangeTracker.Entries<IHasDomainEvents>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified)
            .Select(e => e.Entity)
            .Where(e => e.DomainEvents.Count > 0)
            .ToList();
        if (sources.Count == 0)
        {
            return;
        }

        var now = clock.GetUtcNow();
        var publish = options.Value.Transport == MessageTransport.RabbitMq;
        foreach (var source in sources)
        {
            if (publish)
            {
                foreach (var domainEvent in source.DomainEvents)
                {
                    if (OutboxMessages.From(domainEvent, now) is { } message)
                    {
                        context.Add(message);
                    }
                }
            }

            source.ClearDomainEvents();
        }
    }
}
