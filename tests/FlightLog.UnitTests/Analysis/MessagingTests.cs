using System.Text.Json;
using FlightLog.Application.Messaging;
using FlightLog.Application.Processing;
using FlightLog.Contracts;
using FlightLog.Domain.Analysis;
using FlightLog.Domain.Flights;

namespace FlightLog.UnitTests.Analysis;

public sealed class MessagingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Queued_files_and_runs_become_versioned_messages()
    {
        var fileId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var message = OutboxMessages.From(new LogFileQueued(fileId, teamId, Now.AddMinutes(1)), Now)!;

        message.Type.Should().Be(MessageTypes.ProcessLogFileV1);
        message.AggregateId.Should().Be(fileId);
        message.AvailableAt.Should().Be(Now.AddMinutes(1), "a retry is not sent before its due time");
        var payload = JsonSerializer.Deserialize<ProcessLogFileV1>(message.Payload, OutboxMessages.JsonOptions)!;
        payload.LogFileId.Should().Be(fileId);
        payload.TeamId.Should().Be(teamId);
        payload.CorrelationId.Should().Be(message.CorrelationId);

        var runId = Guid.NewGuid();
        var analysis = OutboxMessages.From(new AnalysisRunQueued(runId, Guid.NewGuid(), teamId, null), Now)!;
        analysis.Type.Should().Be(MessageTypes.AnalyzeFlightV1);
        analysis.AvailableAt.Should().Be(Now);
        MessageTypes.ClrTypeOf(analysis.Type).Should().Be<AnalyzeFlightV1>();
        MessageTypes.ClrTypeOf("flightlog.unknown.v9").Should().BeNull();
    }

    [Fact]
    public void Retry_delay_doubles_up_to_the_maximum()
    {
        var options = new ProcessingOptions { RetryBaseDelay = TimeSpan.FromSeconds(30), RetryMaxDelay = TimeSpan.FromMinutes(2) };

        Enumerable.Range(1, 5).Select(options.RetryDelay).Should().Equal(
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(120));
    }
}
