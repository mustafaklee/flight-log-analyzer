using System.Net;
using System.Net.Http.Json;
using FlightLog.IntegrationTests.Infrastructure;

namespace FlightLog.IntegrationTests;

public sealed class AircraftTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object Body(string name, string? identifier = null, uint? version = null, string[]? tags = null) => new
    {
        name,
        identifier,
        type = "Multicopter",
        autopilot = "Px4",
        firmwareName = (string?)null,
        firmwareVersion = "v1.15.0",
        flightController = "Pixhawk 6C",
        hardware = (string?)null,
        description = (string?)null,
        tags = tags ?? ["test"],
        version,
    };

    private static async Task<Aircraft> CreateAsync(HttpClient client, Guid teamId, string name, string? identifier = null) =>
        await (await client.PostAsJsonAsync($"/api/v1/teams/{teamId}/aircraft", Body(name, identifier), Ct)).ReadAsync<Aircraft>(HttpStatusCode.Created);

    [Fact]
    public async Task Aircraft_can_be_created_listed_updated_and_deleted()
    {
        var client = await factory.CreateUserClientAsync();
        var (team, _) = await client.CreateTeamAndProjectAsync();

        var created = await CreateAsync(client, team.Id, "Quad-01", "SN-1");
        created.FirmwareName.Should().BeNull("unknown values are not invented");
        created.Tags.Should().Equal(["test"]);

        var list = await (await client.GetAsync($"/api/v1/teams/{team.Id}/aircraft?search=quad", Ct)).ReadAsync<Paged<Aircraft>>(HttpStatusCode.OK);
        list.Items.Should().ContainSingle().Which.Id.Should().Be(created.Id);

        var updated = await (await client.PutAsJsonAsync($"/api/v1/aircraft/{created.Id}", Body("Quad-01b", "SN-1", created.Version, ["a", "A", "b"]), Ct))
            .ReadAsync<Aircraft>(HttpStatusCode.OK);
        updated.Name.Should().Be("Quad-01b");
        updated.Tags.Should().Equal(["a", "b"]);
        updated.Version.Should().NotBe(created.Version);

        var stale = await client.PutAsJsonAsync($"/api/v1/aircraft/{created.Id}", Body("Stale", "SN-1", created.Version), Ct);
        (await stale.ReadAsync<Problem>(HttpStatusCode.Conflict)).Code.Should().Be("aircraft.version_mismatch");

        (await client.DeleteAsync($"/api/v1/aircraft/{created.Id}", Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.GetAsync($"/api/v1/aircraft/{created.Id}", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Identifiers_are_unique_within_a_team_only()
    {
        var client = await factory.CreateUserClientAsync();
        var (team, _) = await client.CreateTeamAndProjectAsync();
        var (otherTeam, _) = await client.CreateTeamAndProjectAsync();
        await CreateAsync(client, team.Id, "A", "SN-42");

        var duplicate = await client.PostAsJsonAsync($"/api/v1/teams/{team.Id}/aircraft", Body("B", "SN-42"), Ct);
        (await duplicate.ReadAsync<Problem>(HttpStatusCode.Conflict)).Code.Should().Be("aircraft.identifier_taken");

        await CreateAsync(client, otherTeam.Id, "C", "SN-42");
        await CreateAsync(client, team.Id, "No identifier 1");
        await CreateAsync(client, team.Id, "No identifier 2");
    }

    [Fact]
    public async Task Viewers_can_read_but_not_write_and_outsiders_see_nothing()
    {
        var owner = await factory.CreateUserClientAsync();
        var (team, _) = await owner.CreateTeamAndProjectAsync();
        var aircraft = await CreateAsync(owner, team.Id, "Quad");

        var viewerEmail = $"viewer-{Guid.NewGuid():N}@flightlog.test";
        var viewer = await factory.CreateUserClientAsync(viewerEmail);
        await owner.AddMemberAsync(team.Id, viewerEmail, "Viewer");
        var outsider = await factory.CreateUserClientAsync();

        (await viewer.GetAsync($"/api/v1/aircraft/{aircraft.Id}", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await viewer.PostAsJsonAsync($"/api/v1/teams/{team.Id}/aircraft", Body("X"), Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await viewer.DeleteAsync($"/api/v1/aircraft/{aircraft.Id}", Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await outsider.GetAsync($"/api/v1/aircraft/{aircraft.Id}", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await outsider.GetAsync($"/api/v1/teams/{team.Id}/aircraft", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await outsider.PutAsJsonAsync($"/api/v1/aircraft/{aircraft.Id}", Body("Hijack", version: aircraft.Version), Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Deleting_an_aircraft_keeps_its_flights()
    {
        var client = await factory.CreateUserClientAsync();
        var (team, project) = await client.CreateTeamAndProjectAsync();
        var aircraft = await CreateAsync(client, team.Id, "Quad");
        var upload = await (await client.UploadLogAsync(project.Id, Fixtures.ULogLike(seed: 77), "a.ulg", $"aircraftId={aircraft.Id}"))
            .ReadAsync<UploadResult>(HttpStatusCode.Created);
        upload.Flight.AircraftId.Should().Be(aircraft.Id);

        (await client.DeleteAsync($"/api/v1/aircraft/{aircraft.Id}", Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var flight = await (await client.GetAsync($"/api/v1/flights/{upload.Flight.Id}", Ct)).ReadAsync<Flight>(HttpStatusCode.OK);
        flight.AircraftId.Should().BeNull();
    }

    [Fact]
    public async Task Invalid_aircraft_is_rejected_with_validation_errors()
    {
        var client = await factory.CreateUserClientAsync();
        var (team, _) = await client.CreateTeamAndProjectAsync();

        var response = await client.PostAsJsonAsync($"/api/v1/teams/{team.Id}/aircraft", Body(""), Ct);

        (await response.ReadAsync<Problem>(HttpStatusCode.BadRequest)).Errors.Should().ContainKey("name");
    }
}
