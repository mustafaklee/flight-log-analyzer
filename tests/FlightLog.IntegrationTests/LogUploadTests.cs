using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using FlightLog.Application.Abstractions;
using FlightLog.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace FlightLog.IntegrationTests;

public sealed class LogUploadTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Real_px4_log_is_stored_hashed_and_queued()
    {
        var client = await factory.CreateUserClientAsync();
        var (team, project) = await client.CreateTeamAndProjectAsync();
        var bytes = Fixtures.Px4Sample;

        var response = await client.UploadLogAsync(project.Id, bytes, "sample.ulg");

        var result = await response.ReadAsync<UploadResult>(HttpStatusCode.Created);
        result.LogFile.Format.Should().Be("Px4ULog");
        result.LogFile.Status.Should().Be("Queued");
        result.LogFile.SizeBytes.Should().Be(bytes.Length);
        result.LogFile.Sha256.Should().Be(Convert.ToHexStringLower(SHA256.HashData(bytes)));
        result.LogFile.FileName.Should().Be("sample.ulg");
        result.Flight.TeamId.Should().Be(team.Id);
        result.Flight.ProjectId.Should().Be(project.Id);
        result.Flight.Autopilot.Should().Be("Px4");
        result.Flight.Name.Should().Be("sample");

        // Parsing happens in later stages: nothing is invented about the flight yet.
        result.Flight.StartedAt.Should().BeNull();
        result.Flight.DurationSeconds.Should().BeNull();
        result.Flight.FirmwareVersion.Should().BeNull();

        var download = await client.GetAsync($"/api/v1/log-files/{result.LogFile.Id}/content", Ct);
        download.StatusCode.Should().Be(HttpStatusCode.OK);
        (await download.Content.ReadAsByteArrayAsync(Ct)).Should().Equal(bytes);
        download.Content.Headers.ContentDisposition!.FileName.Should().Be("sample.ulg");
    }

    [Fact]
    public async Task DataFlash_content_is_detected_as_ardupilot()
    {
        var client = await factory.CreateUserClientAsync();
        var (_, project) = await client.CreateTeamAndProjectAsync();

        var result = await (await client.UploadLogAsync(project.Id, Fixtures.DataFlashLike(), "00000042.BIN", "name=Hover%20test"))
            .ReadAsync<UploadResult>(HttpStatusCode.Created);

        result.LogFile.Format.Should().Be("ArduPilotDataFlash");
        result.Flight.Autopilot.Should().Be("ArduPilot");
        result.Flight.Name.Should().Be("Hover test");
    }

    [Theory]
    [InlineData("notes.txt", "log_file.unsupported_extension")]
    [InlineData("flight", "log_file.unsupported_extension")]
    public async Task Unsupported_extensions_are_rejected(string fileName, string code)
    {
        var client = await factory.CreateUserClientAsync();
        var (_, project) = await client.CreateTeamAndProjectAsync();

        var response = await client.UploadLogAsync(project.Id, Fixtures.ULogLike(), fileName);

        (await response.ReadAsync<Problem>(HttpStatusCode.UnprocessableEntity)).Code.Should().Be(code);
    }

    [Fact]
    public async Task Content_is_validated_not_only_the_extension()
    {
        var client = await factory.CreateUserClientAsync();
        var (_, project) = await client.CreateTeamAndProjectAsync();
        var text = "this is not a flight log, just text"u8.ToArray();

        var notALog = await client.UploadLogAsync(project.Id, text, "fake.ulg");
        var mismatch = await client.UploadLogAsync(project.Id, Fixtures.ULogLike(), "renamed.bin");
        var empty = await client.UploadLogAsync(project.Id, [], "empty.ulg");

        (await notALog.ReadAsync<Problem>(HttpStatusCode.UnprocessableEntity)).Code.Should().Be("log_file.unrecognized_content");
        (await mismatch.ReadAsync<Problem>(HttpStatusCode.UnprocessableEntity)).Code.Should().Be("log_file.extension_mismatch");
        (await empty.ReadAsync<Problem>(HttpStatusCode.UnprocessableEntity)).Code.Should().Be("log_file.empty");

        var flights = await (await client.GetAsync($"/api/v1/flights?projectId={project.Id}", Ct)).ReadAsync<Paged<FlightListItem>>(HttpStatusCode.OK);
        flights.TotalCount.Should().Be(0, "rejected uploads must not leave records behind");
    }

    [Fact]
    public async Task Duplicate_upload_in_the_same_team_points_to_the_existing_flight()
    {
        var client = await factory.CreateUserClientAsync();
        var (_, project) = await client.CreateTeamAndProjectAsync();
        var bytes = Fixtures.ULogLike(seed: 7);
        var first = await (await client.UploadLogAsync(project.Id, bytes, "a.ulg")).ReadAsync<UploadResult>(HttpStatusCode.Created);

        var again = await client.UploadLogAsync(project.Id, bytes, "renamed-copy.ulg");

        var problem = await again.ReadAsync<Problem>(HttpStatusCode.Conflict);
        problem.Code.Should().Be("log_file.duplicate");
        problem.FlightId.Should().Be(first.Flight.Id);
        problem.LogFileId.Should().Be(first.LogFile.Id);
    }

    [Fact]
    public async Task Same_content_in_another_team_is_a_separate_record_sharing_storage()
    {
        var bytes = Fixtures.ULogLike(seed: 11);
        var clientA = await factory.CreateUserClientAsync();
        var clientB = await factory.CreateUserClientAsync();
        var (_, projectA) = await clientA.CreateTeamAndProjectAsync();
        var (_, projectB) = await clientB.CreateTeamAndProjectAsync();

        var a = await (await clientA.UploadLogAsync(projectA.Id, bytes, "a.ulg")).ReadAsync<UploadResult>(HttpStatusCode.Created);
        var b = await (await clientB.UploadLogAsync(projectB.Id, bytes, "b.ulg")).ReadAsync<UploadResult>(HttpStatusCode.Created);

        b.LogFile.Id.Should().NotBe(a.LogFile.Id);
        b.LogFile.Sha256.Should().Be(a.LogFile.Sha256);

        // Each team sees only its own record.
        (await clientB.GetAsync($"/api/v1/log-files/{a.LogFile.Id}", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Deleting team A's flight must keep the shared object for team B.
        (await clientA.DeleteAsync($"/api/v1/flights/{a.Flight.Id}", Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var download = await clientB.GetAsync($"/api/v1/log-files/{b.LogFile.Id}/content", Ct);
        (await download.Content.ReadAsByteArrayAsync(Ct)).Should().Equal(bytes);
    }

    [Fact]
    public async Task Deleting_the_last_flight_of_a_content_removes_the_object()
    {
        var client = await factory.CreateUserClientAsync();
        var (_, project) = await client.CreateTeamAndProjectAsync();
        var bytes = Fixtures.ULogLike(seed: 21);
        var upload = await (await client.UploadLogAsync(project.Id, bytes, "x.ulg")).ReadAsync<UploadResult>(HttpStatusCode.Created);
        var storage = factory.Services.GetRequiredService<IFileStorage>();
        var key = $"raw/{upload.LogFile.Sha256}";
        (await storage.ExistsAsync(key, Ct)).Should().BeTrue();

        (await client.DeleteAsync($"/api/v1/flights/{upload.Flight.Id}", Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await storage.ExistsAsync(key, Ct)).Should().BeFalse();
        (await client.GetAsync($"/api/v1/log-files/{upload.LogFile.Id}", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync($"/api/v1/flights/{upload.Flight.Id}", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Client_file_names_cannot_traverse_paths()
    {
        var client = await factory.CreateUserClientAsync();
        var (_, project) = await client.CreateTeamAndProjectAsync();

        var result = await (await client.UploadLogAsync(project.Id, Fixtures.ULogLike(seed: 3), "../../etc/..\\passwd.ulg"))
            .ReadAsync<UploadResult>(HttpStatusCode.Created);

        result.LogFile.FileName.Should().Be("passwd.ulg");
    }

    [Fact]
    public async Task Upload_requires_engineer_role_and_team_membership()
    {
        var owner = await factory.CreateUserClientAsync();
        var (team, project) = await owner.CreateTeamAndProjectAsync();
        var viewerEmail = $"viewer-{Guid.NewGuid():N}@flightlog.test";
        var viewer = await factory.CreateUserClientAsync(viewerEmail);
        await owner.AddMemberAsync(team.Id, viewerEmail, "Viewer");
        var outsider = await factory.CreateUserClientAsync();

        (await viewer.UploadLogAsync(project.Id, Fixtures.ULogLike(), "v.ulg")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await outsider.UploadLogAsync(project.Id, Fixtures.ULogLike(), "o.ulg")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Aircraft_of_another_team_cannot_be_linked()
    {
        var client = await factory.CreateUserClientAsync();
        var (_, project) = await client.CreateTeamAndProjectAsync();
        var other = await factory.CreateUserClientAsync();
        var (otherTeam, _) = await other.CreateTeamAndProjectAsync();
        var foreignAircraft = await (await other.PostAsJsonAsync($"/api/v1/teams/{otherTeam.Id}/aircraft", new { name = "Foreign", type = "Multicopter", autopilot = "Px4" }, Ct))
            .ReadAsync<Aircraft>(HttpStatusCode.Created);

        var response = await client.UploadLogAsync(project.Id, Fixtures.ULogLike(seed: 5), "f.ulg", $"aircraftId={foreignAircraft.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Cancel_and_retry_follow_the_status_rules()
    {
        var client = await factory.CreateUserClientAsync();
        var (_, project) = await client.CreateTeamAndProjectAsync();
        var upload = await (await client.UploadLogAsync(project.Id, Fixtures.ULogLike(seed: 9), "c.ulg")).ReadAsync<UploadResult>(HttpStatusCode.Created);
        var id = upload.LogFile.Id;

        var retryQueued = await client.PostAsync($"/api/v1/log-files/{id}/retry", null, Ct);
        var cancelled = await (await client.PostAsync($"/api/v1/log-files/{id}/cancel", null, Ct)).ReadAsync<LogFile>(HttpStatusCode.OK);
        var cancelAgain = await client.PostAsync($"/api/v1/log-files/{id}/cancel", null, Ct);
        var retried = await (await client.PostAsync($"/api/v1/log-files/{id}/retry", null, Ct)).ReadAsync<LogFile>(HttpStatusCode.OK);

        (await retryQueued.ReadAsync<Problem>(HttpStatusCode.UnprocessableEntity)).Code.Should().Be("log_file.not_retryable");
        cancelled.Status.Should().Be("Cancelled");
        (await cancelAgain.ReadAsync<Problem>(HttpStatusCode.UnprocessableEntity)).Code.Should().Be("log_file.not_cancellable");
        retried.Status.Should().Be("Queued");
    }

    [Fact]
    public async Task Requests_without_a_file_are_rejected()
    {
        var client = await factory.CreateUserClientAsync();
        var (_, project) = await client.CreateTeamAndProjectAsync();

        var json = await client.PostAsJsonAsync($"/api/v1/projects/{project.Id}/logs", new { file = "x" }, Ct);
        var form = new MultipartFormDataContent { { new StringContent("value"), "other" } };
        var noFile = await client.PostAsync($"/api/v1/projects/{project.Id}/logs", form, Ct);

        (await json.ReadAsync<Problem>(HttpStatusCode.BadRequest)).Code.Should().Be("upload.not_multipart");
        (await noFile.ReadAsync<Problem>(HttpStatusCode.BadRequest)).Code.Should().Be("upload.missing_file");
    }

    [Fact]
    public async Task Project_with_flights_cannot_be_deleted()
    {
        var client = await factory.CreateUserClientAsync();
        var (_, project) = await client.CreateTeamAndProjectAsync();
        await client.UploadLogAsync(project.Id, Fixtures.ULogLike(seed: 31), "p.ulg");

        var response = await client.DeleteAsync($"/api/v1/projects/{project.Id}", Ct);

        (await response.ReadAsync<Problem>(HttpStatusCode.Conflict)).Code.Should().Be("project.has_flights");
    }
}

public sealed class SmallUploadLimitFactory : ApiFactory
{
    protected override long MaxUploadBytes => 1024;
}

public sealed class UploadLimitTests(SmallUploadLimitFactory factory) : IClassFixture<SmallUploadLimitFactory>
{
    [Fact]
    public async Task Files_over_the_configured_limit_are_rejected_without_records()
    {
        var client = await factory.CreateUserClientAsync();
        var (_, project) = await client.CreateTeamAndProjectAsync();

        var response = await client.UploadLogAsync(project.Id, Fixtures.ULogLike(), "big.ulg");

        (await response.ReadAsync<Problem>(HttpStatusCode.RequestEntityTooLarge)).Code.Should().Be("upload.too_large");
        var flights = await (await client.GetAsync($"/api/v1/flights?projectId={project.Id}", TestContext.Current.CancellationToken))
            .ReadAsync<Paged<FlightListItem>>(HttpStatusCode.OK);
        flights.TotalCount.Should().Be(0);
    }
}
