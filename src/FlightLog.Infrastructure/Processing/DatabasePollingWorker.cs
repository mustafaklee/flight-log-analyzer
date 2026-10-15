using FlightLog.Application.Processing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlightLog.Infrastructure.Processing;

/// <summary>
/// Database transport (<c>Messaging:Transport = Database</c>): polls PostgreSQL for queued files and analysis
/// runs and executes them one at a time. Several workers can run side by side: claiming is a compare-and-set on
/// the row version. With RabbitMQ, <see cref="Messaging.RabbitMqWorker"/> is used instead.
/// </summary>
internal sealed partial class DatabasePollingWorker(
    WorkRunner runner,
    IOptions<ProcessingOptions> options,
    TimeProvider clock,
    ILogger<DatabasePollingWorker> logger) : BackgroundService
{
    private readonly string _workerId = $"{Environment.MachineName}:{Environment.ProcessId}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(_workerId);
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var worked = await runner.RunOnceAsync(_workerId, stoppingToken);
                failures = 0;
                if (!worked)
                {
                    await Task.Delay(options.Value.PollInterval, clock, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Database or storage unavailable: back off and try again rather than stopping the host.
                failures++;
                LogLoopFailed(ex);
                var delay = TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Min(failures, 6))));
                try
                {
                    await Task.Delay(delay, clock, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        LogStopped(_workerId);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Database polling worker {WorkerId} started")]
    private partial void LogStarted(string workerId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Database polling worker {WorkerId} stopped")]
    private partial void LogStopped(string workerId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Polling loop failed; retrying")]
    private partial void LogLoopFailed(Exception exception);
}
