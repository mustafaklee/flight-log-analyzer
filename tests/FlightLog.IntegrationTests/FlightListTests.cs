using System.Net;
using System.Net.Http.Json;
using FlightLog.IntegrationTests.Infrastructure;

namespace FlightLog.IntegrationTests;

public sealed class FlightListTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<UploadResult> UploadAsync(HttpClient client, Guid projectId, byte[] content, string fileName, string? query = null) =>
        await (await client.UploadLogAsync(projectId, content, fileName, query)).ReadAsync<UploadResult>(HttpStatusCode.Created);

    [Fact]
    public async Task List_filters_searches_sorts_and_pages()
    {
        var client = await factory.CreateUserClientAsync();
        var (team, alpha) = await client.CreateTeamAndProjectAsync("Alpha");
        var beta = await (await client.PostAsJsonAsync($"/api/v1/teams/{team.Id}/projects", new { name = "Beta" }, Ct)).ReadAsync<Project>(HttpStatusCode.Created);

        var px4 = await UploadAsync(client, alpha.Id, Fixtures.ULogLike(seed: 1), "hover.ulg", "name=Hover%20test");
        var ardu = await UploadAsync(client, alpha.Id, Fixtures.DataFlashLike(seed: 2), "mission.bin", "name=Mission%20run");
        var other = await UploadAsync(client, beta.Id, Fixtures.ULogLike(seed: 3), "zeta.ulg");

        var all = await GetAsync(client, $"teamId={team.Id}");
        all.TotalCount.Should().Be(3);
        all.Items.Select(i => i.Id).Should().Equal([other.Flight.Id, ardu.Flight.Id, px4.Flight.Id], "newest first by default");
        all.Items.Should().OnlyContain(i => i.ProcessingStatus == "Queued" && i.DurationSeconds == null && i.StartedAt == null);

        (await GetAsync(client, $"projectId={alpha.Id}")).TotalCount.Should().Be(2);
        (await GetAsync(client, $"teamId={team.Id}&autopilot=ArduPilot")).Items.Should().ContainSingle().Which.Id.Should().Be(ardu.Flight.Id);
        (await GetAsync(client, $"teamId={team.Id}&search=HOVER")).Items.Should().ContainSingle().Which.Id.Should().Be(px4.Flight.Id);
        (await GetAsync(client, $"teamId={team.Id}&search=zeta.ulg")).Items.Should().ContainSingle().Which.Id.Should().Be(other.Flight.Id);
        (await GetAsync(client, $"teamId={team.Id}&status=Failed")).TotalCount.Should().Be(0);
        (await GetAsync(client, $"teamId={team.Id}&from={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(1).ToString("O"))}")).TotalCount.Should().Be(0);

        var byName = await GetAsync(client, $"teamId={team.Id}&sort=NameAsc");
        byName.Items.Select(i => i.Name).Should().BeInAscendingOrder(StringComparer.Ordinal);

        var page2 = await GetAsync(client, $"teamId={team.Id}&pageSize=2&page=2");
        page2.Items.Should().ContainSingle();
        page2.TotalPages.Should().Be(2);
    }

    [Fact]
    public async Task Users_only_see_flights_of_their_teams()
    {
        var mine = await factory.CreateUserClientAsync();
        var theirs = await factory.CreateUserClientAsync();
        var (myTeam, myProject) = await mine.CreateTeamAndProjectAsync();
        var (theirTeam, theirProject) = await theirs.CreateTeamAndProjectAsync();
        await UploadAsync(mine, myProject.Id, Fixtures.ULogLike(seed: 10), "m.ulg");
        var foreign = await UploadAsync(theirs, theirProject.Id, Fixtures.ULogLike(seed: 11), "t.ulg");

        var visible = await GetAsync(mine, string.Empty);
        visible.Items.Should().OnlyContain(i => i.TeamId == myTeam.Id);

        (await mine.GetAsync($"/api/v1/flights?teamId={theirTeam.Id}", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await mine.GetAsync($"/api/v1/flights/{foreign.Flight.Id}", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await mine.GetAsync($"/api/v1/log-files/{foreign.LogFile.Id}/content", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await mine.DeleteAsync($"/api/v1/flights/{foreign.Flight.Id}", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Flight_details_can_be_edited_with_optimistic_concurrency()
    {
        var client = await factory.CreateUserClientAsync();
        var (_, project) = await client.CreateTeamAndProjectAsync();
        var upload = await UploadAsync(client, project.Id, Fixtures.ULogLike(seed: 20), "e.ulg");

        var updated = await (await client.PutAsJsonAsync($"/api/v1/flights/{upload.Flight.Id}",
            new { name = "Renamed", notes = "Wind 8 m/s", aircraftId = (Guid?)null, version = upload.Flight.Version }, Ct)).ReadAsync<Flight>(HttpStatusCode.OK);
        updated.Name.Should().Be("Renamed");
        updated.Notes.Should().Be("Wind 8 m/s");

        var stale = await client.PutAsJsonAsync($"/api/v1/flights/{upload.Flight.Id}",
            new { name = "Stale", notes = (string?)null, aircraftId = (Guid?)null, version = upload.Flight.Version }, Ct);
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    private static async Task<Paged<FlightListItem>> GetAsync(HttpClient client, string query) =>
        await (await client.GetAsync($"/api/v1/flights?{query}", Ct)).ReadAsync<Paged<FlightListItem>>(HttpStatusCode.OK);
}
