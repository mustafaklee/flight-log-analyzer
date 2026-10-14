using System.Net;
using System.Net.Http.Json;
using FlightLog.IntegrationTests.Infrastructure;

namespace FlightLog.IntegrationTests;

/// <summary>
/// Verifies FR-A7: team data is isolated in the backend. A user outside a team cannot read or change its
/// resources by guessing ids, and members can only do what their role allows.
/// </summary>
public sealed class TeamAccessTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Creator_becomes_project_manager_and_sees_the_team()
    {
        var owner = await factory.CreateUserClientAsync();

        var team = await (await owner.PostAsJsonAsync("/api/v1/teams", new { name = "Flight Test" }, Ct)).ReadAsync<Team>(HttpStatusCode.Created);
        var teams = await (await owner.GetAsync("/api/v1/teams", Ct)).ReadAsync<Team[]>(HttpStatusCode.OK);

        var me = await (await owner.GetAsync("/api/v1/auth/me", Ct)).ReadAsync<Me>(HttpStatusCode.OK);

        team.MyRole.Should().Be("ProjectManager");
        team.MemberCount.Should().Be(1);
        teams.Should().ContainSingle(t => t.Id == team.Id);
        me.Teams.Should().ContainSingle().Which.Should().Be(new Membership(team.Id, "Flight Test", "ProjectManager"));
    }

    [Fact]
    public async Task Outsider_cannot_read_or_modify_another_teams_resources_by_id()
    {
        var (owner, team, project) = await CreateTeamWithProjectAsync();
        var outsider = await factory.CreateUserClientAsync();

        var responses = new[]
        {
            await outsider.GetAsync($"/api/v1/teams/{team.Id}", Ct),
            await outsider.GetAsync($"/api/v1/teams/{team.Id}/members", Ct),
            await outsider.GetAsync($"/api/v1/teams/{team.Id}/projects", Ct),
            await outsider.PostAsJsonAsync($"/api/v1/teams/{team.Id}/projects", new { name = "Hijack" }, Ct),
            await outsider.PostAsJsonAsync($"/api/v1/teams/{team.Id}/members", new { email = "x@flightlog.test", role = "Viewer" }, Ct),
            await outsider.GetAsync($"/api/v1/projects/{project.Id}", Ct),
            await outsider.PutAsJsonAsync($"/api/v1/projects/{project.Id}", new { name = "Renamed", version = project.Version }, Ct),
            await outsider.DeleteAsync($"/api/v1/projects/{project.Id}", Ct),
        };

        // 404, not 403: the existence of other teams' resources must not leak.
        responses.Select(r => r.StatusCode).Should().AllBeEquivalentTo(HttpStatusCode.NotFound);
        (await (await outsider.GetAsync("/api/v1/teams", Ct)).ReadAsync<Team[]>(HttpStatusCode.OK)).Should().BeEmpty();

        // And nothing changed.
        var unchanged = await (await owner.GetAsync($"/api/v1/projects/{project.Id}", Ct)).ReadAsync<Project>(HttpStatusCode.OK);
        unchanged.Name.Should().Be(project.Name);
    }

    [Fact]
    public async Task Missing_and_foreign_resources_return_identical_responses()
    {
        var (_, _, project) = await CreateTeamWithProjectAsync();
        var outsider = await factory.CreateUserClientAsync();

        var foreign = await (await outsider.GetAsync($"/api/v1/projects/{project.Id}", Ct)).ReadAsync<Problem>(HttpStatusCode.NotFound);
        var missing = await (await outsider.GetAsync($"/api/v1/projects/{Guid.NewGuid()}", Ct)).ReadAsync<Problem>(HttpStatusCode.NotFound);

        foreign.Title.Should().Be(missing.Title);
        foreign.Detail.Should().Be(missing.Detail);
        foreign.Code.Should().Be("not_found");
    }

    [Fact]
    public async Task Viewer_can_read_but_not_write()
    {
        var (owner, team, project) = await CreateTeamWithProjectAsync();
        var viewerEmail = $"viewer-{Guid.NewGuid():N}@flightlog.test";
        var viewer = await factory.CreateUserClientAsync(viewerEmail);
        await AddMemberAsync(owner, team.Id, viewerEmail, "Viewer");

        (await viewer.GetAsync($"/api/v1/projects/{project.Id}", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await viewer.GetAsync($"/api/v1/teams/{team.Id}/members", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        var create = await viewer.PostAsJsonAsync($"/api/v1/teams/{team.Id}/projects", new { name = "Mine" }, Ct);
        var update = await viewer.PutAsJsonAsync($"/api/v1/projects/{project.Id}", new { name = "Mine", version = project.Version }, Ct);
        var delete = await viewer.DeleteAsync($"/api/v1/projects/{project.Id}", Ct);
        var addMember = await viewer.PostAsJsonAsync($"/api/v1/teams/{team.Id}/members", new { email = viewerEmail, role = "ProjectManager" }, Ct);

        new[] { create, update, delete, addMember }.Select(r => r.StatusCode).Should().AllBeEquivalentTo(HttpStatusCode.Forbidden);
        (await create.ReadAsync<Problem>(HttpStatusCode.Forbidden)).Code.Should().Be("forbidden");
    }

    [Fact]
    public async Task Project_manager_manages_roles_and_cannot_leave_as_the_last_manager()
    {
        var owner = await factory.CreateUserClientAsync();
        var ownerId = await owner.CurrentUserIdAsync();
        var team = await (await owner.PostAsJsonAsync("/api/v1/teams", new { name = "Roles" }, Ct)).ReadAsync<Team>(HttpStatusCode.Created);
        var memberEmail = $"member-{Guid.NewGuid():N}@flightlog.test";
        var member = await factory.CreateUserClientAsync(memberEmail, "Member");
        var added = await AddMemberAsync(owner, team.Id, memberEmail, "Engineer");

        var leaveAlone = await owner.DeleteAsync($"/api/v1/teams/{team.Id}/members/{ownerId}", Ct);
        (await leaveAlone.ReadAsync<Problem>(HttpStatusCode.UnprocessableEntity)).Code.Should().Be("team.last_project_manager");

        var promoted = await (await owner.PutAsJsonAsync($"/api/v1/teams/{team.Id}/members/{added.UserId}", new { role = "ProjectManager" }, Ct))
            .ReadAsync<TeamMember>(HttpStatusCode.OK);
        promoted.Role.Should().Be("ProjectManager");

        // With a second manager in place the owner may leave; the remaining member keeps the team.
        (await owner.DeleteAsync($"/api/v1/teams/{team.Id}/members/{ownerId}", Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await owner.GetAsync($"/api/v1/teams/{team.Id}", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var members = await (await member.GetAsync($"/api/v1/teams/{team.Id}/members", Ct)).ReadAsync<TeamMember[]>(HttpStatusCode.OK);
        members.Should().ContainSingle().Which.Email.Should().Be(memberEmail);
    }

    [Fact]
    public async Task Adding_unknown_or_existing_members_is_reported()
    {
        var owner = await factory.CreateUserClientAsync();
        var team = await (await owner.PostAsJsonAsync("/api/v1/teams", new { name = "Members" }, Ct)).ReadAsync<Team>(HttpStatusCode.Created);
        var email = $"m-{Guid.NewGuid():N}@flightlog.test";
        await factory.CreateUserClientAsync(email);
        await AddMemberAsync(owner, team.Id, email, "Viewer");

        var unknown = await owner.PostAsJsonAsync($"/api/v1/teams/{team.Id}/members", new { email = "ghost@flightlog.test", role = "Viewer" }, Ct);
        var duplicate = await owner.PostAsJsonAsync($"/api/v1/teams/{team.Id}/members", new { email, role = "Engineer" }, Ct);
        var badRole = await owner.PostAsJsonAsync($"/api/v1/teams/{team.Id}/members", new { email, role = 99 }, Ct);

        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await duplicate.ReadAsync<Problem>(HttpStatusCode.UnprocessableEntity)).Code.Should().Be("team.member_exists");
        badRole.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Administrator_can_read_any_team()
    {
        var (_, team, project) = await CreateTeamWithProjectAsync();
        var admin = await factory.CreateAuthenticatedClientAsync(ApiFactory.AdminEmail, ApiFactory.AdminPassword);

        (await admin.GetAsync($"/api/v1/teams/{team.Id}", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.GetAsync($"/api/v1/projects/{project.Id}", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task<(HttpClient Owner, Team Team, Project Project)> CreateTeamWithProjectAsync()
    {
        var owner = await factory.CreateUserClientAsync();
        var team = await (await owner.PostAsJsonAsync("/api/v1/teams", new { name = "Team " + Guid.NewGuid().ToString("N")[..6] }, Ct))
            .ReadAsync<Team>(HttpStatusCode.Created);
        var project = await (await owner.PostAsJsonAsync($"/api/v1/teams/{team.Id}/projects", new { name = "Campaign", description = "Endurance" }, Ct))
            .ReadAsync<Project>(HttpStatusCode.Created);
        return (owner, team, project);
    }

    private static async Task<TeamMember> AddMemberAsync(HttpClient owner, Guid teamId, string email, string role) =>
        await (await owner.PostAsJsonAsync($"/api/v1/teams/{teamId}/members", new { email, role }, Ct))
            .ReadAsync<TeamMember>(HttpStatusCode.Created);
}
