using System.Text.Json;
using FlightLog.Contracts;
using FlightLog.Domain.Analysis;
using FlightLog.Domain.Common;
using FlightLog.Domain.Flights;

namespace FlightLog.Application.Messaging;

/// <summary>Turns domain events into the messages the workers consume.</summary>
public static class OutboxMessages
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The message announcing the event, or null when the event needs no message.</summary>
    public static OutboxMessage? From(IDomainEvent domainEvent, DateTimeOffset now) => domainEvent switch
    {
        LogFileQueued e => Create(new ProcessLogFileV1(e.LogFileId, e.TeamId, NewCorrelationId()), e.LogFileId, now, e.NotBefore),
        AnalysisRunQueued e => Create(new AnalyzeFlightV1(e.AnalysisRunId, e.FlightId, e.TeamId, NewCorrelationId()), e.AnalysisRunId, now, e.NotBefore),
        AiAnalysisQueued e => Create(new AiAnalyzeFlightV1(e.AiAnalysisId, e.FlightId, e.TeamId, NewCorrelationId()), e.AiAnalysisId, now, e.NotBefore),
        _ => null,
    };

    public static OutboxMessage Create(IWorkMessage message, Guid aggregateId, DateTimeOffset now, DateTimeOffset? availableAt)
    {
        ArgumentNullException.ThrowIfNull(message);
        return OutboxMessage.Create(
            MessageTypes.Of(message), aggregateId, JsonSerializer.Serialize(message, message.GetType(), JsonOptions), message.CorrelationId, now, availableAt);
    }

    private static string NewCorrelationId() => Guid.NewGuid().ToString("N");
}
