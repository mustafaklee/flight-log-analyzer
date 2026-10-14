using System.Net;
using System.Net.Http.Json;
using FlightLog.IntegrationTests.Infrastructure;

namespace FlightLog.IntegrationTests;

public sealed class AdminUsersTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<HttpClient> AdminAsync() => factory.CreateAuthenticatedClientAsync(ApiFactory.AdminEmail, ApiFactory.AdminPassword);

    [Fact]
    public async Task Administrator_creates_accounts_that_can_sign_in()
    {
        var admin = await AdminAsync();
        var email = $"pilot-{Guid.NewGuid():N}@flightlog.test";

        var created = await (await admin.PostAsJsonAsync("/api/v1/admin/users", new { email, displayName = "Pilot", password = "Pilot-Password-1" }, Ct))
            .ReadAsync<AdminUser>(HttpStatusCode.Created);
        created.IsAdministrator.Should().BeFalse();
        created.IsDisabled.Should().BeFalse();

        var list = await (await admin.GetAsync($"/api/v1/admin/users?search={email}", Ct)).ReadAsync<Paged<AdminUser>>(HttpStatusCode.OK);
        list.Items.Should().ContainSingle().Which.Id.Should().Be(created.Id);

        await factory.CreateClient().LoginAsync(email, "Pilot-Password-1");
    }

    [Fact]
    public async Task Weak_passwords_and_duplicate_emails_are_rejected()
    {
        var admin = await AdminAsync();
        var email = $"dup-{Guid.NewGuid():N}@flightlog.test";
        await (await admin.PostAsJsonAsync("/api/v1/admin/users", new { email, displayName = "A", password = "Pilot-Password-1" }, Ct)).ReadAsync<AdminUser>(HttpStatusCode.Created);

        (await admin.PostAsJsonAsync("/api/v1/admin/users", new { email, displayName = "B", password = "Pilot-Password-1" }, Ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.PostAsJsonAsync("/api/v1/admin/users", new { email = "x" + email, displayName = "C", password = "short" }, Ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Disabled_accounts_cannot_sign_in_or_refresh_until_enabled()
    {
        var admin = await AdminAsync();
        var email = $"disable-{Guid.NewGuid():N}@flightlog.test";
        var user = await (await admin.PostAsJsonAsync("/api/v1/admin/users", new { email, displayName = "D", password = "Pilot-Password-1" }, Ct))
            .ReadAsync<AdminUser>(HttpStatusCode.Created);
        var tokens = await factory.CreateClient().LoginAsync(email, "Pilot-Password-1");

        var disabled = await (await admin.PostAsync($"/api/v1/admin/users/{user.Id}/disable", null, Ct)).ReadAsync<AdminUser>(HttpStatusCode.OK);
        disabled.IsDisabled.Should().BeTrue();

        var anonymous = factory.CreateClient();
        (await anonymous.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "Pilot-Password-1" }, Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = tokens.RefreshToken }, Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        (await (await admin.PostAsync($"/api/v1/admin/users/{user.Id}/enable", null, Ct)).ReadAsync<AdminUser>(HttpStatusCode.OK)).IsDisabled.Should().BeFalse();
        await anonymous.LoginAsync(email, "Pilot-Password-1");
    }

    [Fact]
    public async Task Administrators_cannot_disable_themselves()
    {
        var admin = await AdminAsync();
        var me = await admin.CurrentUserIdAsync();

        var response = await admin.PostAsync($"/api/v1/admin/users/{me}/disable", null, Ct);

        (await response.ReadAsync<Problem>(HttpStatusCode.UnprocessableEntity)).Code.Should().Be("user.cannot_disable_self");
    }

    [Fact]
    public async Task Regular_users_cannot_use_admin_endpoints()
    {
        var client = await factory.CreateUserClientAsync();

        (await client.GetAsync("/api/v1/admin/users", Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.PostAsJsonAsync("/api/v1/admin/users", new { email = "a@b.c", displayName = "A", password = "Pilot-Password-1" }, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await factory.CreateClient().GetAsync("/api/v1/admin/users", Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
