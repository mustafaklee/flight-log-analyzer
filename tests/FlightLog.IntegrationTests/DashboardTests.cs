using System.Net;
using FlightLog.IntegrationTests.Infrastructure;

namespace FlightLog.IntegrationTests;

public sealed class DashboardTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Dashboard_summarizes_the_team()
    {
        var client = await factory.CreateUserClientAsync();
        var (team, project) = await client.CreateTeamAndProjectAsync();

        var empty = await (await client.GetAsync($"/api/v1/teams/{team.Id}/dashboard", Ct)).ReadAsync<Dashboard>(HttpStatusCode.OK);
        empty.FlightCount.Should().Be(0);
        empty.ProjectCount.Should().Be(1);
        empty.RecentFlights.Should().BeEmpty();
        empty.FindingsBySeverity.Values.Should().AllBeEquivalentTo(0);

        var sitl = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "ardupilot", "sitl-copter-4.6.3.bin"));
        await client.UploadLogAsync(project.Id, sitl, "sitl.bin");
        await client.UploadLogAsync(project.Id, Fixtures.Px4Sample, "sample.ulg");
        await factory.ProcessQueueAsync();

        var dashboard = await (await client.GetAsync($"/api/v1/teams/{team.Id}/dashboard", Ct)).ReadAsync<Dashboard>(HttpStatusCode.OK);
        dashboard.FlightCount.Should().Be(2);
        dashboard.TotalFlightHours.Should().BeGreaterThan(0);
        dashboard.FilesByStatus.Should().Equal(new Dictionary<string, int> { ["Completed"] = 2 });
        dashboard.RecentFlights.Should().HaveCount(2).And.OnlyContain(f => f.LatestAnalysis != null);
        dashboard.FindingsBySeverity["Warning"].Should().BeGreaterThan(0);
        dashboard.RecentFindings.Should().NotBeEmpty().And.OnlyContain(f => f.Severity != "Info");
        dashboard.RecentFindings.Should().Contain(f => f.RuleId == "data.gaps" && f.FlightName.Length > 0);

        var outsider = await factory.CreateUserClientAsync();
        (await outsider.GetAsync($"/api/v1/teams/{team.Id}/dashboard", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private sealed record DashboardFinding(Guid FindingId, Guid RunId, Guid FlightId, string FlightName, string RuleId, string Title, string Severity, long StartUs);

    private sealed record Dashboard(
        int ProjectCount, int AircraftCount, int FlightCount, int FlightsLast30Days, double TotalFlightHours, Dictionary<string, int> FilesByStatus,
        Dictionary<string, int> FindingsBySeverity, int FlightsWithCriticalFindings, FlightWithAnalysis[] RecentFlights, DashboardFinding[] RecentFindings);
}
