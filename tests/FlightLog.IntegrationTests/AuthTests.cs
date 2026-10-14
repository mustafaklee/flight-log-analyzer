using System.Net;
using System.Net.Http.Json;
using FlightLog.IntegrationTests.Infrastructure;

namespace FlightLog.IntegrationTests;

public sealed class AuthTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Registered_user_can_log_in_and_read_profile()
    {
        var email = $"pilot-{Guid.NewGuid():N}@flightlog.test";
        var client = await factory.CreateUserClientAsync(email, "Ayşe Pilot");

        var me = await (await client.GetAsync("/api/v1/auth/me", Ct)).ReadAsync<Me>(HttpStatusCode.OK);

        me.Email.Should().Be(email);
        me.DisplayName.Should().Be("Ayşe Pilot");
        me.Roles.Should().BeEmpty();
        me.Teams.Should().BeEmpty();
    }

    [Fact]
    public async Task Protected_endpoints_require_authentication()
    {
        var response = await factory.CreateClient().GetAsync("/api/v1/teams", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Wrong_password_and_unknown_user_get_the_same_answer()
    {
        var email = $"user-{Guid.NewGuid():N}@flightlog.test";
        await factory.CreateUserClientAsync(email);
        var client = factory.CreateClient();

        var wrongPassword = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "Wrong-Password-1" }, Ct);
        var unknownUser = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = "nobody@flightlog.test", password = "Wrong-Password-1" }, Ct);

        var first = await wrongPassword.ReadAsync<Problem>(HttpStatusCode.Unauthorized);
        var second = await unknownUser.ReadAsync<Problem>(HttpStatusCode.Unauthorized);
        first.Detail.Should().Be(second.Detail);
    }

    [Fact]
    public async Task Account_is_locked_after_repeated_failures()
    {
        var email = $"user-{Guid.NewGuid():N}@flightlog.test";
        await factory.CreateUserClientAsync(email);
        var client = factory.CreateClient();

        for (var i = 0; i < 5; i++)
        {
            await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "Wrong-Password-1" }, Ct);
        }

        var correctPassword = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = ApiClientExtensions.DefaultPassword }, Ct);
        correctPassword.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Refresh_token_issues_a_working_access_token()
    {
        var email = $"user-{Guid.NewGuid():N}@flightlog.test";
        await factory.CreateUserClientAsync(email);
        var client = factory.CreateClient();
        var tokens = await client.LoginAsync(email, ApiClientExtensions.DefaultPassword);

        var refreshed = await (await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = tokens.RefreshToken }, Ct))
            .ReadAsync<TokenResponse>(HttpStatusCode.OK);

        refreshed.AccessToken.Should().NotBeNullOrEmpty().And.NotBe(tokens.AccessToken);
        client.DefaultRequestHeaders.Authorization = new("Bearer", refreshed.AccessToken);
        (await client.GetAsync("/api/v1/auth/me", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Invalid_refresh_token_is_rejected()
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = "not-a-token" }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Access_token_cannot_be_used_as_refresh_token()
    {
        var email = $"user-{Guid.NewGuid():N}@flightlog.test";
        await factory.CreateUserClientAsync(email);
        var client = factory.CreateClient();
        var tokens = await client.LoginAsync(email, ApiClientExtensions.DefaultPassword);

        var response = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = tokens.AccessToken }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Registration_validates_input_and_rejects_duplicates()
    {
        var client = factory.CreateClient();
        var email = $"dup-{Guid.NewGuid():N}@flightlog.test";

        var invalid = await client.PostAsJsonAsync("/api/v1/auth/register", new { email = "nope", password = "", displayName = "" }, Ct);
        var weakPassword = await client.PostAsJsonAsync("/api/v1/auth/register", new { email, password = "short", displayName = "X" }, Ct);
        await client.PostAsJsonAsync("/api/v1/auth/register", new { email, password = ApiClientExtensions.DefaultPassword, displayName = "X" }, Ct);
        var duplicate = await client.PostAsJsonAsync("/api/v1/auth/register", new { email, password = ApiClientExtensions.DefaultPassword, displayName = "X" }, Ct);

        (await invalid.ReadAsync<Problem>(HttpStatusCode.BadRequest)).Errors.Should().ContainKeys("email", "password", "displayName");
        (await weakPassword.ReadAsync<Problem>(HttpStatusCode.BadRequest)).Errors.Should().ContainKey("password");
        (await duplicate.ReadAsync<Problem>(HttpStatusCode.BadRequest)).Errors.Should().ContainKey("email");
    }

    [Fact]
    public async Task Bootstrap_administrator_is_created_with_the_administrator_role()
    {
        var client = await factory.CreateAuthenticatedClientAsync(ApiFactory.AdminEmail, ApiFactory.AdminPassword);

        var me = await (await client.GetAsync("/api/v1/auth/me", Ct)).ReadAsync<Me>(HttpStatusCode.OK);

        me.Roles.Should().Equal("Administrator");
    }
}

public sealed class RegistrationDisabledFactory : ApiFactory
{
    protected override bool AllowSelfRegistration => false;
}

public sealed class RegistrationDisabledTests(RegistrationDisabledFactory factory) : IClassFixture<RegistrationDisabledFactory>
{
    [Fact]
    public async Task Self_registration_can_be_disabled()
    {
        var response = await factory.CreateClient().PostAsJsonAsync(
            "/api/v1/auth/register",
            new { email = "someone@flightlog.test", password = ApiClientExtensions.DefaultPassword, displayName = "Someone" },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}

public sealed class LowRateLimitFactory : ApiFactory
{
    protected override int AuthRateLimitPerMinute => 3;
}

public sealed class RateLimitTests(LowRateLimitFactory factory) : IClassFixture<LowRateLimitFactory>
{
    [Fact]
    public async Task Login_endpoint_is_rate_limited()
    {
        var client = factory.CreateClient();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            var response = await client.PostAsJsonAsync(
                "/api/v1/auth/login",
                new { email = "nobody@flightlog.test", password = "Wrong-Password-1" },
                TestContext.Current.CancellationToken);
            statuses.Add(response.StatusCode);
        }

        statuses.Should().Equal(HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests);
    }
}
