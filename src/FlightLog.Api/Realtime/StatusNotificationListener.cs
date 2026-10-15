using System.Text.Json;
using FlightLog.Infrastructure.Messaging;
using Microsoft.AspNetCore.SignalR;
using Npgsql;

namespace FlightLog.Api.Realtime;

/// <summary>
/// Listens on the PostgreSQL status channel and forwards each notification to the team's SignalR group (ADR-0011).
/// Reconnects with backoff; notifications sent while disconnected are lost, which clients tolerate by re-polling.
/// </summary>
internal sealed partial class StatusNotificationListener(
    IConfiguration configuration,
    IHubContext<ProcessingHub, IProcessingClient> hub,
    TimeProvider clock,
    ILogger<StatusNotificationListener> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var connectionString = configuration.GetConnectionString("Postgres")!;
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync(stoppingToken);
                connection.Notification += (_, e) => Forward(e.Payload);
                await using (var listen = new NpgsqlCommand($"LISTEN {StatusNotifications.Channel}", connection))
                {
                    await listen.ExecuteNonQueryAsync(stoppingToken);
                }

                LogListening();
                failures = 0;
                while (!stoppingToken.IsCancellationRequested)
                {
                    await connection.WaitAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is NpgsqlException or System.Net.Sockets.SocketException or TimeoutException or InvalidOperationException)
            {
                failures++;
                LogListenFailed(ex, failures);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(failures, 5)))), clock, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private void Forward(string payload)
    {
        StatusNotification? notification;
        try
        {
            notification = JsonSerializer.Deserialize<StatusNotification>(payload, StatusNotifications.JsonOptions);
        }
        catch (JsonException)
        {
            notification = null;
        }

        if (notification is null)
        {
            LogUnreadable();
            return;
        }

        // Fire and forget: the Npgsql notification callback is synchronous, and SignalR sends are buffered per connection.
        _ = SendAsync(notification);
    }

    private async Task SendAsync(StatusNotification notification)
    {
        try
        {
            await hub.Clients.Groups(ProcessingHub.TeamGroup(notification.TeamId), ProcessingHub.AdministratorsGroup).StatusChanged(notification);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogSendFailed(ex, notification.Id);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Listening for processing status notifications")]
    private partial void LogListening();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Status notification listener failed ({Failures} in a row); reconnecting")]
    private partial void LogListenFailed(Exception ex, int failures);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ignored an unreadable status notification")]
    private partial void LogUnreadable();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not forward the status notification of {Id}")]
    private partial void LogSendFailed(Exception ex, Guid id);
}
