using System.Text.Json;
using FlightLog.Application.Messaging;
using FlightLog.Application.Processing;
using FlightLog.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace FlightLog.Infrastructure.Messaging;

/// <summary>How the consumer settles a delivery.</summary>
public enum DeliveryDecision
{
    /// <summary>Done (including "nothing to do" for duplicates): remove from the queue.</summary>
    Ack,

    /// <summary>Unreadable, or retries used up: move to the dead-letter queue.</summary>
    DeadLetter,

    /// <summary>Could not be handled now (database down): give back for another try.</summary>
    Requeue,
}

/// <summary>
/// Consumes the processing queue and runs the work through <see cref="WorkRunner"/>. Settlement:
/// unreadable or unknown messages and work whose automatic retries are used up go to the dead-letter queue;
/// everything else is acknowledged after the work ended, because the outcome (including a scheduled retry) is
/// already recorded in PostgreSQL. A failure before the work could be recorded is requeued once, then
/// dead-lettered; the outbox maintenance announces such work again later (PostgreSQL is the source of truth).
/// </summary>
internal sealed partial class RabbitMqWorker(
    RabbitMqConnection connection,
    WorkRunner runner,
    IOptions<RabbitMqOptions> options,
    TimeProvider clock,
    ILogger<RabbitMqWorker> logger) : BackgroundService
{
    private readonly string _workerId = $"{Environment.MachineName}:{Environment.ProcessId}";

    /// <summary>Handles one message; separated from the RabbitMQ plumbing for tests.</summary>
    public async Task<DeliveryDecision> HandleAsync(string? type, ReadOnlyMemory<byte> body, CancellationToken stoppingToken)
    {
        IWorkMessage? message = null;
        var clrType = MessageTypes.ClrTypeOf(type);
        if (clrType is not null)
        {
            try
            {
                message = JsonSerializer.Deserialize(body.Span, clrType, OutboxMessages.JsonOptions) as IWorkMessage;
            }
            catch (JsonException)
            {
                message = null;
            }
        }

        var outcome = message switch
        {
            ProcessLogFileV1 m when m.LogFileId != Guid.Empty => await runner.ProcessFileAsync(m.LogFileId, _workerId, stoppingToken),
            AnalyzeFlightV1 m when m.AnalysisRunId != Guid.Empty => await runner.AnalyzeAsync(m.AnalysisRunId, _workerId, stoppingToken),
            _ => (WorkOutcome?)null,
        };
        if (outcome is null)
        {
            LogUnreadable(type ?? "(none)");
            return DeliveryDecision.DeadLetter;
        }

        return outcome == WorkOutcome.RetriesExhausted ? DeliveryDecision.DeadLetter : DeliveryDecision.Ack;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(_workerId);
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConsumeAsync(stoppingToken);
                failures = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failures++;
                LogConsumerFailed(ex);
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

        LogStopped(_workerId);
    }

    /// <summary>Consumes until the channel closes or the host stops.</summary>
    private async Task ConsumeAsync(CancellationToken stoppingToken)
    {
        var conn = await connection.GetAsync(stoppingToken);
        await using var channel = await conn.CreateChannelAsync(cancellationToken: stoppingToken);
        await RabbitMqTopology.DeclareAsync(channel, stoppingToken);
        await channel.BasicQosAsync(0, Math.Max((ushort)1, options.Value.PrefetchCount), global: false, stoppingToken);

        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.ChannelShutdownAsync += (_, _) =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, delivery) => await OnDeliveryAsync(channel, delivery, stoppingToken);
        await channel.BasicConsumeAsync(RabbitMqTopology.Queue, autoAck: false, consumer, stoppingToken);

        await Task.WhenAny(closed.Task, Task.Delay(Timeout.Infinite, stoppingToken));
        stoppingToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException("The RabbitMQ channel was closed.");
    }

    private async Task OnDeliveryAsync(IChannel channel, BasicDeliverEventArgs delivery, CancellationToken stoppingToken)
    {
        DeliveryDecision decision;
        try
        {
            decision = await HandleAsync(delivery.BasicProperties.Type, delivery.Body, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            decision = DeliveryDecision.Requeue;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Nothing could be recorded (database unreachable while claiming). Try once more, then dead-letter.
            LogHandlingFailed(ex, delivery.BasicProperties.MessageId);
            decision = delivery.Redelivered ? DeliveryDecision.DeadLetter : DeliveryDecision.Requeue;
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), clock, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                decision = DeliveryDecision.Requeue;
            }
        }

        try
        {
            switch (decision)
            {
                case DeliveryDecision.Ack:
                    await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, CancellationToken.None);
                    break;
                case DeliveryDecision.DeadLetter:
                    await channel.BasicRejectAsync(delivery.DeliveryTag, requeue: false, CancellationToken.None);
                    break;
                default:
                    await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, CancellationToken.None);
                    break;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The channel is gone; the broker redelivers the message, which is harmless.
            LogSettleFailed(ex, delivery.BasicProperties.MessageId);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "RabbitMQ worker {WorkerId} started")]
    private partial void LogStarted(string workerId);

    [LoggerMessage(Level = LogLevel.Information, Message = "RabbitMQ worker {WorkerId} stopped")]
    private partial void LogStopped(string workerId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dead-lettered an unreadable message of type {Type}")]
    private partial void LogUnreadable(string type);

    [LoggerMessage(Level = LogLevel.Error, Message = "Consuming from RabbitMQ failed; reconnecting")]
    private partial void LogConsumerFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Handling message {MessageId} failed")]
    private partial void LogHandlingFailed(Exception exception, string? messageId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Settling message {MessageId} failed; the broker will redeliver it")]
    private partial void LogSettleFailed(Exception exception, string? messageId);
}

/// <summary>Runs recovery and outbox maintenance once a minute (the database poller does it inline).</summary>
internal sealed partial class MaintenanceWorker(WorkRunner runner, TimeProvider clock, ILogger<MaintenanceWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await runner.RunMaintenanceIfDueAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(15), clock, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LogFailed(ex);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), clock, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Processing maintenance failed; retrying")]
    private partial void LogFailed(Exception exception);
}

/// <summary>Queue depths for the processing statistics.</summary>
internal sealed class RabbitMqQueueMonitor(RabbitMqConnection connection) : Application.Abstractions.IMessageQueueMonitor
{
    public async Task<IReadOnlyList<Application.Abstractions.QueueDepth>> GetDepthsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var conn = await connection.GetAsync(cancellationToken);
            await using var channel = await conn.CreateChannelAsync(cancellationToken: cancellationToken);
            await RabbitMqTopology.DeclareAsync(channel, cancellationToken);
            var result = new List<Application.Abstractions.QueueDepth>();
            foreach (var queue in new[] { RabbitMqTopology.Queue, RabbitMqTopology.DeadLetterQueue })
            {
                var ok = await channel.QueueDeclarePassiveAsync(queue, cancellationToken);
                result.Add(new Application.Abstractions.QueueDepth(queue, ok.MessageCount, ok.ConsumerCount));
            }

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException)
        {
            return [];
        }
    }
}

internal sealed class NoBrokerQueueMonitor : Application.Abstractions.IMessageQueueMonitor
{
    public Task<IReadOnlyList<Application.Abstractions.QueueDepth>> GetDepthsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Application.Abstractions.QueueDepth>>([]);
}
