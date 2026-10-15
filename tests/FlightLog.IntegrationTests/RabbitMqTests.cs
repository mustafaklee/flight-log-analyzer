using System.Net;
using System.Text;
using System.Text.Json;
using FlightLog.Application.Messaging;
using FlightLog.Contracts;
using FlightLog.Infrastructure.Messaging;
using FlightLog.Infrastructure.Persistence;
using FlightLog.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;

namespace FlightLog.IntegrationTests;

/// <summary>
/// The production path (ADR-0006): outbox rows forwarded to a real RabbitMQ, the worker consuming them in the
/// background, unreadable messages dead-lettered and duplicate deliveries harmless.
/// </summary>
public sealed class RabbitMqTests(RabbitMqTests.BrokerApiFactory factory) : IClassFixture<RabbitMqTests.BrokerApiFactory>
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(90);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Uploaded_log_is_processed_and_analyzed_through_the_broker()
    {
        var client = await factory.CreateUserClientAsync();
        var (_, project) = await client.CreateTeamAndProjectAsync();
        var upload = await (await client.UploadLogAsync(project.Id, Fixtures.Px4Sample, "sample.ulg")).ReadAsync<UploadResult>(HttpStatusCode.Created);

        var file = await WaitForFileAsync(client, upload.LogFile.Id, "Completed");

        file.FailureCode.Should().BeNull();
        var run = (await (await client.GetAsync($"/api/v1/flights/{upload.Flight.Id}/analysis-runs", Ct)).ReadAsync<AnalysisRun[]>(HttpStatusCode.OK)).Single();
        run.Status.Should().Be("Succeeded");

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FlightLogDbContext>();
        var messages = await db.OutboxMessages.AsNoTracking().Where(m => m.AggregateId == upload.LogFile.Id || m.AggregateId == run.Id).ToListAsync(Ct);
        messages.Select(m => m.Type).Should().BeEquivalentTo([MessageTypes.ProcessLogFileV1, MessageTypes.AnalyzeFlightV1]);
        messages.Should().OnlyContain(m => m.PublishedAt != null && m.PublishAttempts == 1);

        var admin = await factory.CreateAuthenticatedClientAsync(ApiFactory.AdminEmail, ApiFactory.AdminPassword);
        var stats = await (await admin.GetAsync("/api/v1/admin/processing/stats", Ct)).ReadAsync<BrokerStats>(HttpStatusCode.OK);
        stats.Queues.Should().Contain(q => q.Queue == "flightlog.processing").And.Contain(q => q.Queue == "flightlog.processing.dlq");

        (await factory.CreateClient().GetAsync("/health/ready", Ct)).StatusCode.Should().Be(HttpStatusCode.OK, "the broker is part of readiness");
    }

    [Fact]
    public async Task Unreadable_message_goes_to_the_dead_letter_queue()
    {
        await using var connection = await factory.ConnectAsync();
        await using var channel = await connection.CreateChannelAsync(cancellationToken: Ct);
        await RabbitMqTopology.DeclareAsync(channel, Ct);
        var before = await channel.MessageCountAsync(RabbitMqTopology.DeadLetterQueue, Ct);

        await PublishAsync(channel, "flightlog.unknown.v1", "{}");
        await PublishAsync(channel, MessageTypes.ProcessLogFileV1, "not json");

        await WaitUntilAsync(async () => await channel.MessageCountAsync(RabbitMqTopology.DeadLetterQueue, Ct) == before + 2, "both messages are dead-lettered");
        (await channel.MessageCountAsync(RabbitMqTopology.Queue, Ct)).Should().Be(0);
    }

    [Fact]
    public async Task Duplicate_messages_are_acknowledged_without_new_work()
    {
        var client = await factory.CreateUserClientAsync();
        var (_, project) = await client.CreateTeamAndProjectAsync();
        var upload = await (await client.UploadLogAsync(project.Id, Fixtures.Px4Sample, "sample.ulg")).ReadAsync<UploadResult>(HttpStatusCode.Created);
        await WaitForFileAsync(client, upload.LogFile.Id, "Completed");
        var run = (await (await client.GetAsync($"/api/v1/flights/{upload.Flight.Id}/analysis-runs", Ct)).ReadAsync<AnalysisRun[]>(HttpStatusCode.OK)).Single();

        await using var connection = await factory.ConnectAsync();
        await using var channel = await connection.CreateChannelAsync(cancellationToken: Ct);
        var deadLettered = await channel.MessageCountAsync(RabbitMqTopology.DeadLetterQueue, Ct);
        var teamId = upload.Flight.TeamId;
        await PublishAsync(channel, MessageTypes.ProcessLogFileV1, JsonSerializer.Serialize(new ProcessLogFileV1(upload.LogFile.Id, teamId, "duplicate"), OutboxMessages.JsonOptions));
        await PublishAsync(channel, MessageTypes.AnalyzeFlightV1, JsonSerializer.Serialize(new AnalyzeFlightV1(run.Id, upload.Flight.Id, teamId, "duplicate"), OutboxMessages.JsonOptions));

        await WaitUntilAsync(async () => await channel.MessageCountAsync(RabbitMqTopology.Queue, Ct) == 0, "the duplicates are consumed");
        await Task.Delay(TimeSpan.FromSeconds(1), Ct);

        (await channel.MessageCountAsync(RabbitMqTopology.DeadLetterQueue, Ct)).Should().Be(deadLettered, "a duplicate is not an error");
        var jobs = await (await client.GetAsync($"/api/v1/log-files/{upload.LogFile.Id}/jobs", Ct)).ReadAsync<Job[]>(HttpStatusCode.OK);
        jobs.Should().ContainSingle();
        var runs = await (await client.GetAsync($"/api/v1/flights/{upload.Flight.Id}/analysis-runs", Ct)).ReadAsync<AnalysisRun[]>(HttpStatusCode.OK);
        runs.Should().ContainSingle().Which.Attempt.Should().Be(run.Attempt);
    }

    private static async Task PublishAsync(IChannel channel, string type, string body)
    {
        var properties = new BasicProperties { MessageId = Guid.NewGuid().ToString("N"), Type = type, ContentType = "application/json", DeliveryMode = DeliveryModes.Persistent };
        await channel.BasicPublishAsync(RabbitMqTopology.Exchange, RabbitMqTopology.RoutingKey, mandatory: false, properties, Encoding.UTF8.GetBytes(body), Ct);
    }

    private static async Task<LogFile> WaitForFileAsync(HttpClient client, Guid id, string status)
    {
        LogFile? file = null;
        await WaitUntilAsync(async () =>
        {
            file = await (await client.GetAsync($"/api/v1/log-files/{id}", Ct)).ReadAsync<LogFile>(HttpStatusCode.OK);
            return file.Status == status || file.Status is "Failed" or "Cancelled";
        }, $"the file reaches {status}");
        file!.Status.Should().Be(status, file.FailureMessage);
        return file;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting until {what}.");
            }

            await Task.Delay(200, Ct);
        }
    }

    /// <summary>The API with the broker transport and the background workers running, as in production.</summary>
    public sealed class BrokerApiFactory : ApiFactory
    {
        private readonly string _virtualHost = $"test-{Guid.NewGuid():N}";
        private (string Host, int Port) _broker;

        protected override async Task PrepareAsync()
        {
            _broker = await RabbitMqServer.GetAsync();
            await RabbitMqServer.CreateVirtualHostAsync(_virtualHost);
        }

        protected override void ConfigureMessaging(IWebHostBuilder builder)
        {
            builder.UseSetting("Messaging:Transport", "RabbitMq");
            builder.UseSetting("Processing:Enabled", "true");
            builder.UseSetting("RabbitMq:HostName", _broker.Host);
            builder.UseSetting("RabbitMq:Port", _broker.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.UseSetting("RabbitMq:UserName", RabbitMqServer.UserName);
            builder.UseSetting("RabbitMq:Password", RabbitMqServer.Password);
            builder.UseSetting("RabbitMq:VirtualHost", _virtualHost);
        }

        public Task<IConnection> ConnectAsync() => new ConnectionFactory
        {
            HostName = _broker.Host,
            Port = _broker.Port,
            UserName = RabbitMqServer.UserName,
            Password = RabbitMqServer.Password,
            VirtualHost = _virtualHost,
        }.CreateConnectionAsync(Ct);
    }
}

internal sealed record QueueInfo(string Queue, long Messages, long Consumers);

internal sealed record BrokerStats(int OutboxPending, QueueInfo[] Queues);
