using System.Net;
using System.Net.Http.Json;
using FlightLog.IntegrationTests.Infrastructure;

namespace FlightLog.IntegrationTests;

public sealed class ProjectTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Project_lifecycle_create_read_update_delete()
    {
        var (client, team) = await CreateTeamAsync();

        var createResponse = await client.PostAsJsonAsync($"/api/v1/teams/{team.Id}/projects", new { name = "  Hover tests ", description = "Quad hover" }, Ct);
        var created = await createResponse.ReadAsync<Project>(HttpStatusCode.Created);
        createResponse.Headers.Location!.ToString().Should().EndWith($"/api/v1/projects/{created.Id}");
        created.Name.Should().Be("Hover tests");
        created.TeamId.Should().Be(team.Id);

        var fetched = await (await client.GetAsync($"/api/v1/projects/{created.Id}", Ct)).ReadAsync<Project>(HttpStatusCode.OK);
        fetched.Should().BeEquivalentTo(created, o => o.Using<DateTimeOffset>(c => c.Subject.Should().BeCloseTo(c.Expectation, TimeSpan.FromMilliseconds(1))).WhenTypeIs<DateTimeOffset>());

        var updated = await (await client.PutAsJsonAsync($"/api/v1/projects/{created.Id}", new { name = "Hover v2", description = (string?)null, version = fetched.Version }, Ct))
            .ReadAsync<Project>(HttpStatusCode.OK);
        updated.Name.Should().Be("Hover v2");
        updated.Description.Should().BeNull();

        (await client.DeleteAsync($"/api/v1/projects/{created.Id}", Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.GetAsync($"/api/v1/projects/{created.Id}", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Stale_version_is_rejected_with_conflict()
    {
        var (client, team) = await CreateTeamAsync();
        var project = await CreateProjectAsync(client, team.Id, "Concurrency");

        var first = await client.PutAsJsonAsync($"/api/v1/projects/{project.Id}", new { name = "Edit A", version = project.Version }, Ct);
        var second = await client.PutAsJsonAsync($"/api/v1/projects/{project.Id}", new { name = "Edit B", version = project.Version }, Ct);

        var afterFirst = await first.ReadAsync<Project>(HttpStatusCode.OK);
        afterFirst.Version.Should().NotBe(project.Version, "PostgreSQL xmin changes on every update");
        (await second.ReadAsync<Problem>(HttpStatusCode.Conflict)).Code.Should().Be("project.version_mismatch");
        (await (await client.GetAsync($"/api/v1/projects/{project.Id}", Ct)).ReadAsync<Project>(HttpStatusCode.OK)).Name.Should().Be("Edit A");
    }

    [Fact]
    public async Task Project_names_are_unique_within_a_team_only()
    {
        var (client, team) = await CreateTeamAsync();
        var (otherClient, otherTeam) = await CreateTeamAsync();
        await CreateProjectAsync(client, team.Id, "Shared name");

        var duplicate = await client.PostAsJsonAsync($"/api/v1/teams/{team.Id}/projects", new { name = "Shared name" }, Ct);
        var otherTeamSameName = await otherClient.PostAsJsonAsync($"/api/v1/teams/{otherTeam.Id}/projects", new { name = "Shared name" }, Ct);

        (await duplicate.ReadAsync<Problem>(HttpStatusCode.Conflict)).Code.Should().Be("project.name_taken");
        otherTeamSameName.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Invalid_requests_return_validation_problem_details()
    {
        var (client, team) = await CreateTeamAsync();

        var response = await client.PostAsJsonAsync($"/api/v1/teams/{team.Id}/projects", new { name = "", description = new string('d', 2001) }, Ct);

        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var problem = await response.ReadAsync<Problem>(HttpStatusCode.BadRequest);
        problem.Code.Should().Be("validation_failed");
        problem.TraceId.Should().NotBeNullOrEmpty();
        problem.Errors.Should().ContainKeys("name", "description");
    }

    [Fact]
    public async Task Listing_supports_search_and_paging()
    {
        var (client, team) = await CreateTeamAsync();
        foreach (var name in new[] { "Alpha VTOL", "Bravo quad", "Charlie VTOL", "Delta fixed-wing", "Echo vtol 100%" })
        {
            await CreateProjectAsync(client, team.Id, name);
        }

        var page1 = await (await client.GetAsync($"/api/v1/teams/{team.Id}/projects?pageSize=2&page=1", Ct)).ReadAsync<Paged<Project>>(HttpStatusCode.OK);
        var page3 = await (await client.GetAsync($"/api/v1/teams/{team.Id}/projects?pageSize=2&page=3", Ct)).ReadAsync<Paged<Project>>(HttpStatusCode.OK);
        var search = await (await client.GetAsync($"/api/v1/teams/{team.Id}/projects?search=vtol", Ct)).ReadAsync<Paged<Project>>(HttpStatusCode.OK);
        var wildcard = await (await client.GetAsync($"/api/v1/teams/{team.Id}/projects?search=%25", Ct)).ReadAsync<Paged<Project>>(HttpStatusCode.OK);

        page1.TotalCount.Should().Be(5);
        page1.TotalPages.Should().Be(3);
        page1.Items.Select(p => p.Name).Should().Equal("Alpha VTOL", "Bravo quad");
        page3.Items.Select(p => p.Name).Should().Equal("Echo vtol 100%");
        search.Items.Select(p => p.Name).Should().Equal("Alpha VTOL", "Charlie VTOL", "Echo vtol 100%");
        wildcard.Items.Select(p => p.Name).Should().Equal(["Echo vtol 100%"], "a literal % must not act as a wildcard");
    }

    [Fact]
    public async Task Page_size_is_capped()
    {
        var (client, team) = await CreateTeamAsync();

        var page = await (await client.GetAsync($"/api/v1/teams/{team.Id}/projects?pageSize=100000", Ct)).ReadAsync<Paged<Project>>(HttpStatusCode.OK);

        page.PageSize.Should().Be(100);
    }

    private async Task<(HttpClient Client, Team Team)> CreateTeamAsync()
    {
        var client = await factory.CreateUserClientAsync();
        var team = await (await client.PostAsJsonAsync("/api/v1/teams", new { name = "Projects team" }, Ct)).ReadAsync<Team>(HttpStatusCode.Created);
        return (client, team);
    }

    private static async Task<Project> CreateProjectAsync(HttpClient client, Guid teamId, string name) =>
        await (await client.PostAsJsonAsync($"/api/v1/teams/{teamId}/projects", new { name }, Ct)).ReadAsync<Project>(HttpStatusCode.Created);
}
