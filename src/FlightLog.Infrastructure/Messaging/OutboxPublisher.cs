using System.Text;
using FlightLog.Application.Messaging;
using FlightLog.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace FlightLog.Infrastructure.Messaging;

/// <summary>
/// Forwards outbox rows to RabbitMQ with publisher confirms. Rows are locked with <c>FOR UPDATE SKIP LOCKED</c>, so
/// the API and any number of workers can run a publisher side by side without sending a row twice in parallel.
/// A row is marked published only after the broker confirmed it; a crash in between sends it again, which the
/// consumers tolerate (claiming work is idempotent).
/// </summary>
internal sealed partial class OutboxPublisher(
    IServiceScopeFactory scopes, RabbitMqConnection connection, TimeProvider clock, ILogger<OutboxPublisher> logger) : BackgroundService
{
    private const int BatchSize = 50;
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(1);

    private IChannel? _channel;

    /// <summary>Publishes one batch; returns how many rows were sent. Public for tests.</summary>
    public async Task<int> PublishBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FlightLogDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var now = clock.GetUtcNow();
        var rows = await db.OutboxMessages
            .FromSql($"""SELECT * FROM "OutboxMessages" WHERE "PublishedAt" IS NULL AND "AvailableAt" <= {now} ORDER BY "AvailableAt", "Id" LIMIT {BatchSize} FOR UPDATE SKIP LOCKED""")
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return 0;
        }

        var sent = 0;
        try
        {
            var channel = await ChannelAsync(cancellationToken);
            foreach (var row in rows)
            {
                var properties = new BasicProperties
                {
                    MessageId = row.Id.ToString("N"),
                    Type = row.Type,
                    CorrelationId = row.CorrelationId,
                    ContentType = "application/json",
                    DeliveryMode = DeliveryModes.Persistent,
                    Timestamp = new AmqpTimestamp(row.CreatedAt.ToUnixTimeSeconds()),
                };

                // With confirmation tracking the call returns once the broker confirmed (or throws when it refused).
                await channel.BasicPublishAsync(
                    RabbitMqTopology.Exchange, RabbitMqTopology.RoutingKey, mandatory: true, properties, Encoding.UTF8.GetBytes(row.Payload), cancellationToken);
                row.MarkPublished(clock.GetUtcNow());
                sent++;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException)
        {
            LogPublishFailed(ex);
            rows.FirstOrDefault(r => r.PublishedAt is null)?.MarkFailed(ex.Message);
            await ResetChannelAsync();
        }

        await db.SaveChangesAsync(CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);
        return sent;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await ResetChannelAsync();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var sent = await PublishBatchAsync(stoppingToken);
                failures = 0;
                if (sent < BatchSize)
                {
                    await Task.Delay(IdleDelay, clock, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Database or broker unreachable: back off; the rows stay in the outbox.
                failures++;
                LogLoopFailed(ex);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(failures, 5)))), clock, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task<IChannel> ChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is { IsOpen: true } open)
        {
            return open;
        }

        await ResetChannelAsync();
        var conn = await connection.GetAsync(cancellationToken);
        _channel = await conn.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), cancellationToken);
        await RabbitMqTopology.DeclareAsync(_channel, cancellationToken);
        return _channel;
    }

    private async Task ResetChannelAsync()
    {
        if (_channel is null)
        {
            return;
        }

        try
        {
            await _channel.DisposeAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogPublishFailed(ex);
        }

        _channel = null;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Publishing outbox messages to RabbitMQ failed; they stay in the outbox")]
    private partial void LogPublishFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox publisher loop failed; retrying")]
    private partial void LogLoopFailed(Exception exception);
}
