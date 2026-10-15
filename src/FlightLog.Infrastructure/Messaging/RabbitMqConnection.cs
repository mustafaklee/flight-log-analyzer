using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace FlightLog.Infrastructure.Messaging;

public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    public string HostName { get; set; } = "localhost";

    public int Port { get; set; } = 5672;

    public string UserName { get; set; } = "";

    public string Password { get; set; } = "";

    public string VirtualHost { get; set; } = "/";

    /// <summary>Unacknowledged messages a worker holds; one file at a time keeps memory per worker bounded.</summary>
    public ushort PrefetchCount { get; set; } = 1;
}

/// <summary>Names of exchanges and queues (ADR-0006). Durable; messages are persistent.</summary>
public static class RabbitMqTopology
{
    public const string Exchange = "flightlog.work";
    public const string Queue = "flightlog.processing";
    public const string RoutingKey = "processing";
    public const string DeadLetterExchange = "flightlog.dead-letter";
    public const string DeadLetterQueue = "flightlog.processing.dlq";

    /// <summary>Declares everything; idempotent, so every client does it on connect.</summary>
    public static async Task DeclareAsync(IChannel channel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        await channel.ExchangeDeclareAsync(DeadLetterExchange, ExchangeType.Direct, durable: true, autoDelete: false, cancellationToken: cancellationToken);
        await channel.QueueDeclareAsync(DeadLetterQueue, durable: true, exclusive: false, autoDelete: false, cancellationToken: cancellationToken);
        await channel.QueueBindAsync(DeadLetterQueue, DeadLetterExchange, RoutingKey, cancellationToken: cancellationToken);

        await channel.ExchangeDeclareAsync(Exchange, ExchangeType.Direct, durable: true, autoDelete: false, cancellationToken: cancellationToken);
        await channel.QueueDeclareAsync(
            Queue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                // Rejected messages (unreadable, or retries used up) go to the dead-letter queue.
                ["x-dead-letter-exchange"] = DeadLetterExchange,
                ["x-dead-letter-routing-key"] = RoutingKey,
            },
            cancellationToken: cancellationToken);
        await channel.QueueBindAsync(Queue, Exchange, RoutingKey, cancellationToken: cancellationToken);
    }
}

/// <summary>One connection per process (the client recovers it after network failures); channels are per user.</summary>
public sealed partial class RabbitMqConnection(IOptions<RabbitMqOptions> options, ILogger<RabbitMqConnection> logger) : IAsyncDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IConnection? _connection;

    public async Task<IConnection> GetAsync(CancellationToken cancellationToken)
    {
        if (_connection is { IsOpen: true } open)
        {
            return open;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_connection is { IsOpen: true } current)
            {
                return current;
            }

            if (_connection is not null)
            {
                await _connection.DisposeAsync();
            }

            var o = options.Value;
            var factory = new ConnectionFactory
            {
                HostName = o.HostName,
                Port = o.Port,
                UserName = o.UserName,
                Password = o.Password,
                VirtualHost = o.VirtualHost,
                AutomaticRecoveryEnabled = true,
                TopologyRecoveryEnabled = true,
                ClientProvidedName = $"flightlog:{Environment.MachineName}:{Environment.ProcessId}",
                RequestedConnectionTimeout = TimeSpan.FromSeconds(10),
            };
            _connection = await factory.CreateConnectionAsync(cancellationToken);
            LogConnected(o.HostName, o.Port);
            return _connection;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        _lock.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Connected to RabbitMQ at {Host}:{Port}")]
    private partial void LogConnected(string host, int port);
}
