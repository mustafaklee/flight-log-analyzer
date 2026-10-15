using System.Collections.Concurrent;
using System.Net;
using FlightLog.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace FlightLog.IntegrationTests;

/// <summary>Status changes reach the browsers of the team's members through SignalR (ADR-0011).</summary>
public sealed class RealtimeTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Team_members_receive_every_status_change_of_their_files_and_analyses()
    {
        var owner = await factory.CreateUserClientAsync();
        var (_, project) = await owner.CreateTeamAndProjectAsync();
        var outsider = await factory.CreateUserClientAsync();

        var received = new ConcurrentQueue<Notification>();
        var leaked = new ConcurrentQueue<Notification>();
        await using var ownerHub = await ConnectAsync(owner, received);
        await using var outsiderHub = await ConnectAsync(outsider, leaked);

        var upload = await (await owner.UploadLogAsync(project.Id, Fixtures.Px4Sample, "sample.ulg")).ReadAsync<UploadResult>(HttpStatusCode.Created);
        await factory.ProcessQueueAsync();

        await WaitUntilAsync(() => received.Any(n => n.Kind == "logFile" && n.Status == "Completed"));
        var files = received.Where(n => n.Kind == "logFile").ToList();
        files.Should().OnlyContain(n => n.Id == upload.LogFile.Id && n.FlightId == upload.Flight.Id && n.TeamId == upload.Flight.TeamId);
        files.Select(n => n.Status).Should().ContainInConsecutiveOrder("Processing", "AnalysisPending").And.Contain(["Queued", "Analyzing", "Completed"]);
        var runs = received.Where(n => n.Kind == "analysisRun").Select(n => n.Status).ToList();
        runs.Should().Contain(["Queued", "Running", "Succeeded"]);

        leaked.Should().BeEmpty("the outsider is not a member of the team");
    }

    [Fact]
    public async Task Hub_requires_authentication()
    {
        var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "hubs/processing"), options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
            })
            .Build();

        var connect = () => hub.StartAsync(Ct);

        (await connect.Should().ThrowAsync<HttpRequestException>()).Which.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private async Task<HubConnection> ConnectAsync(HttpClient client, ConcurrentQueue<Notification> sink)
    {
        var token = client.DefaultRequestHeaders.Authorization!.Parameter!;
        var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "hubs/processing"), options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();
        hub.On<Notification>("StatusChanged", sink.Enqueue);
        await hub.StartAsync(Ct);
        return hub;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The expected notification did not arrive.");
            }

            await Task.Delay(100, Ct);
        }
    }

    private sealed record Notification(string Kind, Guid TeamId, Guid FlightId, Guid Id, string Status, string? FailureCode);
}
