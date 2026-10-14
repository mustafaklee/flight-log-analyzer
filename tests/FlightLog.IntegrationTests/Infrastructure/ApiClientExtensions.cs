using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FlightLog.IntegrationTests.Infrastructure;

internal static class ApiClientExtensions
{
    public const string DefaultPassword = "Correct-Horse-9";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task<HttpClient> CreateUserClientAsync(this WebApplicationFactory<Program> factory, string? email = null, string displayName = "Test User")
    {
        email ??= $"user-{Guid.NewGuid():N}@flightlog.test";
        var anonymous = factory.CreateClient();
        var register = await anonymous.PostAsJsonAsync("/api/v1/auth/register", new { email, password = DefaultPassword, displayName });
        register.StatusCode.Should().Be(HttpStatusCode.Created, await register.Content.ReadAsStringAsync());
        return await factory.CreateAuthenticatedClientAsync(email, DefaultPassword);
    }

    public static async Task<HttpClient> CreateAuthenticatedClientAsync(this WebApplicationFactory<Program> factory, string email, string password)
    {
        var client = factory.CreateClient();
        var tokens = await client.LoginAsync(email, password);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }

    public static async Task<TokenResponse> LoginAsync(this HttpClient client, string email, string password)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<TokenResponse>(Json))!;
    }

    public static async Task<T> ReadAsync<T>(this HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(expected, body);
        return JsonSerializer.Deserialize<T>(body, Json)!;
    }

    public static async Task<Guid> CurrentUserIdAsync(this HttpClient client) =>
        (await (await client.GetAsync("/api/v1/auth/me")).ReadAsync<Me>(HttpStatusCode.OK)).Id;
}

internal sealed record TokenResponse(string TokenType, string AccessToken, long ExpiresIn, string RefreshToken);

internal sealed record Me(Guid Id, string Email, string DisplayName, string[] Roles, Membership[] Teams);

internal sealed record Membership(Guid TeamId, string TeamName, string Role);

internal sealed record Team(Guid Id, string Name, DateTimeOffset CreatedAt, string MyRole, int MemberCount);

internal sealed record TeamMember(Guid UserId, string Email, string DisplayName, string Role, DateTimeOffset JoinedAt);

internal sealed record Project(Guid Id, Guid TeamId, string Name, string? Description, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, uint Version);

internal sealed record Paged<T>(T[] Items, int Page, int PageSize, int TotalCount, int TotalPages);

internal sealed record Problem(int Status, string Title, string? Detail, string? Code, string? TraceId, Dictionary<string, string[]>? Errors);
