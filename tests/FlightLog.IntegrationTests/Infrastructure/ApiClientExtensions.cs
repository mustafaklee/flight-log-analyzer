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

internal static class Fixtures
{
    /// <summary>Real PX4 ULog from the pyulog repository (BSD-3-Clause, see fixtures/px4/LICENSE-pyulog.md).</summary>
    public static byte[] Px4Sample => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "px4", "sample.ulg"));

    /// <summary>
    /// Bytes that start like an ArduPilot DataFlash log (message header 0xA3 0x95 followed by the FMT type 128).
    /// Enough for format detection; real DataFlash fixtures arrive with the parser in stage 3.
    /// </summary>
    public static byte[] DataFlashLike(int seed = 1)
    {
        var bytes = new byte[4096];
        new Random(seed).NextBytes(bytes);
        bytes[0] = 0xA3;
        bytes[1] = 0x95;
        bytes[2] = 0x80;
        return bytes;
    }

    /// <summary>A minimal ULog-shaped header (magic, version, timestamp) followed by filler bytes.</summary>
    public static byte[] ULogLike(int seed = 1)
    {
        var bytes = new byte[2048];
        new Random(seed).NextBytes(bytes);
        byte[] magic = [0x55, 0x4C, 0x6F, 0x67, 0x01, 0x12, 0x35, 0x01];
        magic.CopyTo(bytes, 0);
        return bytes;
    }
}

internal static class UploadExtensions
{
    public static Task<HttpResponseMessage> UploadLogAsync(this HttpClient client, Guid projectId, byte[] content, string fileName, string? query = null)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", fileName);
        var url = $"/api/v1/projects/{projectId}/logs" + (query is null ? string.Empty : "?" + query);
        return client.PostAsync(url, form, TestContext.Current.CancellationToken);
    }

    public static async Task<(Team Team, Project Project)> CreateTeamAndProjectAsync(this HttpClient client, string projectName = "Campaign")
    {
        var team = await (await client.PostAsJsonAsync("/api/v1/teams", new { name = "Team " + Guid.NewGuid().ToString("N")[..6] }, TestContext.Current.CancellationToken))
            .ReadAsync<Team>(HttpStatusCode.Created);
        var project = await (await client.PostAsJsonAsync($"/api/v1/teams/{team.Id}/projects", new { name = projectName }, TestContext.Current.CancellationToken))
            .ReadAsync<Project>(HttpStatusCode.Created);
        return (team, project);
    }

    public static async Task AddMemberAsync(this HttpClient owner, Guid teamId, string email, string role) =>
        await (await owner.PostAsJsonAsync($"/api/v1/teams/{teamId}/members", new { email, role }, TestContext.Current.CancellationToken))
            .ReadAsync<TeamMember>(HttpStatusCode.Created);
}

internal sealed record TokenResponse(string TokenType, string AccessToken, long ExpiresIn, string RefreshToken);

internal sealed record Me(Guid Id, string Email, string DisplayName, string[] Roles, Membership[] Teams);

internal sealed record Membership(Guid TeamId, string TeamName, string Role);

internal sealed record Team(Guid Id, string Name, DateTimeOffset CreatedAt, string MyRole, int MemberCount);

internal sealed record TeamMember(Guid UserId, string Email, string DisplayName, string Role, DateTimeOffset JoinedAt);

internal sealed record Project(Guid Id, Guid TeamId, string Name, string? Description, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, uint Version);

internal sealed record Paged<T>(T[] Items, int Page, int PageSize, int TotalCount, int TotalPages);

internal sealed record Problem(int Status, string Title, string? Detail, string? Code, string? TraceId, Dictionary<string, string[]>? Errors, Guid? FlightId = null, Guid? LogFileId = null);

internal sealed record LogFile(Guid Id, Guid FlightId, string FileName, long SizeBytes, string Sha256, string Format, string Status, DateTimeOffset StatusChangedAt, string? FailureCode, string? FailureMessage, Guid UploadedBy, DateTimeOffset UploadedAt);

internal sealed record Flight(Guid Id, Guid TeamId, Guid ProjectId, string ProjectName, Guid? AircraftId, string? AircraftName, string Name, string? Notes, string Autopilot, string? FirmwareVersion, DateTimeOffset? StartedAt, DateTimeOffset OccurredAt, double? DurationSeconds, Guid CreatedBy, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, uint Version, LogFile? LogFile);

internal sealed record FlightListItem(Guid Id, Guid TeamId, Guid ProjectId, string ProjectName, Guid? AircraftId, string? AircraftName, string Name, string Autopilot, string? FirmwareVersion, DateTimeOffset? StartedAt, DateTimeOffset OccurredAt, double? DurationSeconds, string? ProcessingStatus, Guid? LogFileId, string? FileName, long? FileSizeBytes, DateTimeOffset CreatedAt);

internal sealed record UploadResult(Flight Flight, LogFile LogFile);

internal sealed record Aircraft(Guid Id, Guid TeamId, string Name, string? Identifier, string Type, string Autopilot, string? FirmwareName, string? FirmwareVersion, string? FlightController, string? Hardware, string? Description, string[] Tags, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, uint Version);

internal sealed record AdminUser(Guid Id, string Email, string DisplayName, bool IsAdministrator, bool IsDisabled, DateTimeOffset CreatedAt);
