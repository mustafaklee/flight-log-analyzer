using System.Net;
using System.Net.Http.Json;
using FlightLog.Application.Abstractions;
using FlightLog.Application.Processing;
using FlightLog.Domain.Flights;
using FlightLog.Infrastructure.Persistence;
using FlightLog.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FlightLog.IntegrationTests;

/// <summary>Upload, process (as the worker does) and read back real PX4 and ArduPilot logs.</summary>
public sealed class LogProcessingTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Fixture(string path) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", path));

    [Fact]
    public async Task Px4_log_becomes_flight_details_signals_events_and_parameters()
    {
        var client = await factory.CreateUserClientAsync();
        var (_, project) = await client.CreateTeamAndProjectAsync();
        var upload = await (await client.UploadLogAsync(project.Id, Fixture("px4/sample_log_small.ulg"), "sample_log_small.ulg")).ReadAsync<UploadResult>(HttpStatusCode.Created);

        await ProcessQueueAsync();

        var flight = await (await client.GetAsync($"/api/v1/flights/{upload.Flight.Id}", Ct)).ReadAsync<FlightDetails>(HttpStatusCode.OK);
        flight.LogFile!.Status.Should().Be("Completed", "processing is followed by the automatic analysis");
        flight.FirmwareVersion.Should().Be("PX4 v1.11.2 (dev)");
        flight.VehicleType.Should().Be("Vtol");
        flight.Hardware.Should().Be("CUBEPILOT_CUBEORANGE");
        flight.LogStartTimestampUs.Should().Be(20_309_082);
        flight.BootTimeUtc.Should().Be(new DateTimeOffset(2021, 4, 21, 6, 30, 38, TimeSpan.Zero).AddTicks(1_286_970));
        flight.StartedAt.Should().Be(flight.BootTimeUtc!.Value.AddTicks(20_309_082 * 10));
        flight.OccurredAt.Should().Be(flight.StartedAt!.Value);
        flight.DurationSeconds.Should().BeApproximately((1_194_367_328 - 20_309_082) / 1e6, 1e-6);
        flight.LogMetadata.Should().Contain("sys_os_name", "NuttX");

        var signals = await (await client.GetAsync($"/api/v1/flights/{flight.Id}/signals", Ct)).ReadAsync<Signal[]>(HttpStatusCode.OK);
        var roll = signals.Single(s => s.Name == "attitude.roll");
        roll.Kind.Should().Be("Normalized");
        roll.Unit.Should().Be("deg");
        roll.Source!.Message.Should().Be("vehicle_attitude");
        roll.Source.NormalizationVersion.Should().Be("1");
        signals.Should().Contain(s => s.Name == "vehicle_attitude.q[0]" && s.Kind == "Raw");
        signals.Should().Contain(s => s.Name == "gps.lat").And.Contain(s => s.Name == "battery.voltage");
        var normalized = await (await client.GetAsync($"/api/v1/flights/{flight.Id}/signals?kind=Normalized", Ct)).ReadAsync<Signal[]>(HttpStatusCode.OK);
        normalized.Should().OnlyContain(s => s.Kind == "Normalized");

        // Full resolution: every sample comes back, in time order.
        var full = await GetDataAsync(client, flight.Id, "names=attitude.roll&maxPoints=20000");
        var series = full.Signals.Single();
        series.Downsampled.Should().BeFalse();
        series.SamplesInRange.Should().Be(roll.SampleCount);
        series.TimestampsUs.Should().HaveCount((int)roll.SampleCount).And.BeInAscendingOrder();
        series.Values.Where(v => v != null).Max().Should().Be(roll.Max);

        // Downsampled: bounded, extremes kept.
        var reduced = await GetDataAsync(client, flight.Id, "names=attitude.roll,battery.voltage&maxPoints=100");
        reduced.Signals.Should().HaveCount(2);
        reduced.Signals[0].Downsampled.Should().BeTrue();
        reduced.Signals[0].TimestampsUs.Count.Should().BeLessThanOrEqualTo(102);
        reduced.Signals[0].Values.Max().Should().Be(roll.Max);
        reduced.Signals[0].Values.Min().Should().Be(roll.Min);

        // Time window.
        var window = await GetDataAsync(client, flight.Id, "names=attitude.roll&fromUs=21000000&toUs=22000000&maxPoints=20000");
        window.Signals[0].TimestampsUs.Should().NotBeEmpty().And.OnlyContain(t => t >= 21_000_000 && t <= 22_000_000);

        var events = await (await client.GetAsync($"/api/v1/flights/{flight.Id}/events?pageSize=1000", Ct)).ReadAsync<Paged<FlightEvent>>(HttpStatusCode.OK);
        events.Items.Should().ContainSingle(e => e.Kind == "Armed").Which.TimestampUs.Should().Be(20_220_673);
        events.Items.Should().Contain(e => e.Kind == "Message" && e.Text == "[commander] Takeoff detected");
        var armed = await (await client.GetAsync($"/api/v1/flights/{flight.Id}/events?kind=Armed", Ct)).ReadAsync<Paged<FlightEvent>>(HttpStatusCode.OK);
        armed.TotalCount.Should().Be(1);

        var parameters = await (await client.GetAsync($"/api/v1/flights/{flight.Id}/parameters", Ct)).ReadAsync<Parameter[]>(HttpStatusCode.OK);
        parameters.Should().HaveCount(980);
        parameters.Should().OnlyContain(p => p.InitialValue != null);

        var jobs = await (await client.GetAsync($"/api/v1/log-files/{flight.LogFile.Id}/jobs", Ct)).ReadAsync<Job[]>(HttpStatusCode.OK);
        var job = jobs.Should().ContainSingle().Which;
        job.Outcome.Should().Be("Succeeded");
        job.Attempt.Should().Be(1);
        job.ParserName.Should().Be("ulog");
        job.SignalCount.Should().Be(signals.Length);
        job.ParameterCount.Should().Be(980);
        job.ChunkBytes.Should().BePositive();
        job.DurationMs.Should().NotBeNull();
    }

    [Fact]
    public async Task ArduPilot_log_is_processed_and_declared_multipliers_are_applied()
    {
        var client = await factory.CreateUserClientAsync();
        var (_, project) = await client.CreateTeamAndProjectAsync();
        var upload = await (await client.UploadLogAsync(project.Id, Fixture("ardupilot/sitl-copter-4.6.3.bin"), "00000001.BIN")).ReadAsync<UploadResult>(HttpStatusCode.Created);

        await ProcessQueueAsync();

        var flight = await (await client.GetAsync($"/api/v1/flights/{upload.Flight.Id}", Ct)).ReadAsync<FlightDetails>(HttpStatusCode.OK);
        flight.LogFile!.Status.Should().Be("Completed", "processing is followed by the automatic analysis");
        flight.FirmwareVersion.Should().Be("ArduCopter V4.6.3 (92b0cd78)");
        flight.VehicleType.Should().Be("Copter");
        flight.StartedAt.Should().Be(new DateTimeOffset(2026, 10, 14, 23, 30, 35, TimeSpan.Zero).AddTicks(7_906_810).AddTicks(24_509_359 * 10));

        var signals = await (await client.GetAsync($"/api/v1/flights/{flight.Id}/signals?search=gps", Ct)).ReadAsync<Signal[]>(HttpStatusCode.OK);
        var gms = signals.Single(s => s.Name == "GPS.GMS");
        gms.Unit.Should().Be("s");
        gms.Multiplier.Should().Be(0.001);
        var data = await GetDataAsync(client, flight.Id, "names=GPS.GMS&maxPoints=20000");
        data.Signals[0].Values.Max().Should().BeApproximately(gms.Max!.Value, 1e-9);
        gms.Max.Should().BeLessThan(7 * 24 * 3600, "milliseconds of the week are reported in seconds");

        var modes = await (await client.GetAsync($"/api/v1/flights/{flight.Id}/events?kind=ModeChange", Ct)).ReadAsync<Paged<FlightEvent>>(HttpStatusCode.OK);
        modes.Items.Select(e => e.Text).Should().Equal("Mode GUIDED", "Mode GUIDED", "Mode LOITER", "Mode RTL");

        var job = (await (await client.GetAsync($"/api/v1/log-files/{flight.LogFile.Id}/jobs", Ct)).ReadAsync<Job[]>(HttpStatusCode.OK)).Single();
        job.ParserName.Should().Be("dataflash");
        job.Truncated.Should().BeTrue("SITL was stopped while writing the last message");
    }

    [Fact]
    public async Task Damaged_file_fails_with_a_stable_code_and_can_be_retried()
    {
        var client = await factory.CreateUserClientAsync();
        var (_, project) = await client.CreateTeamAndProjectAsync();
        var upload = await (await client.UploadLogAsync(project.Id, Fixtures.ULogLike(seed: 7), "broken.ulg")).ReadAsync<UploadResult>(HttpStatusCode.Created);

        await ProcessQueueAsync();

        var file = await (await client.GetAsync($"/api/v1/log-files/{upload.LogFile.Id}", Ct)).ReadAsync<LogFile>(HttpStatusCode.OK);
        file.Status.Should().Be("Failed");
        file.FailureCode.Should().NotBeNullOrEmpty().And.NotBe("processing.failed", "a damaged file is a known condition, not an internal error");
        file.FailureMessage.Should().NotContain("Exception");

        (await client.PostAsync($"/api/v1/log-files/{file.Id}/retry", null, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        await ProcessQueueAsync();

        var jobs = await (await client.GetAsync($"/api/v1/log-files/{file.Id}/jobs", Ct)).ReadAsync<Job[]>(HttpStatusCode.OK);
        jobs.Select(j => j.Attempt).Should().Equal(2, 1);
        jobs.Should().OnlyContain(j => j.Outcome == "Failed" && j.FailureCode == file.FailureCode);
        var signals = await (await client.GetAsync($"/api/v1/flights/{file.FlightId}/signals", Ct)).ReadAsync<Signal[]>(HttpStatusCode.OK);
        signals.Should().BeEmpty();
    }

    [Fact]
    public async Task Cancelled_and_interrupted_jobs_leave_no_data_and_the_file_can_be_processed_later()
    {
        var client = await factory.CreateUserClientAsync();
        var (_, project) = await client.CreateTeamAndProjectAsync();
        var upload = await (await client.UploadLogAsync(project.Id, Fixtures.Px4Sample, "sample.ulg")).ReadAsync<UploadResult>(HttpStatusCode.Created);
        await ProcessQueueAsync(except: upload.LogFile.Id);

        // Worker shutdown in the middle: the file goes back to the queue.
        var claim = await ClaimAsync(upload.LogFile.Id);
        using (var stopped = new CancellationTokenSource())
        {
            await stopped.CancelAsync();
            await ProcessAsync(claim, stopped.Token, stopped.Token);
        }

        (await GetFileAsync(client, upload.LogFile.Id)).Status.Should().Be("Queued");

        // The user cancels while the worker is busy with it.
        claim = await ClaimAsync(upload.LogFile.Id);
        (await client.PostAsync($"/api/v1/log-files/{upload.LogFile.Id}/cancel", null, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        using (var cancelled = new CancellationTokenSource())
        {
            await cancelled.CancelAsync();
            await ProcessAsync(claim, cancelled.Token, CancellationToken.None);
        }

        (await GetFileAsync(client, upload.LogFile.Id)).Status.Should().Be("Cancelled");
        (await (await client.GetAsync($"/api/v1/flights/{upload.Flight.Id}/signals", Ct)).ReadAsync<Signal[]>(HttpStatusCode.OK)).Should().BeEmpty();

        // Retry processes it normally.
        (await client.PostAsync($"/api/v1/log-files/{upload.LogFile.Id}/retry", null, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        await ProcessQueueAsync();

        (await GetFileAsync(client, upload.LogFile.Id)).Status.Should().Be("Completed");
        var jobs = await (await client.GetAsync($"/api/v1/log-files/{upload.LogFile.Id}/jobs", Ct)).ReadAsync<Job[]>(HttpStatusCode.OK);
        jobs.Select(j => j.Outcome).Should().Equal("Succeeded", "Cancelled", "Cancelled");
        var flight = await (await client.GetAsync($"/api/v1/flights/{upload.Flight.Id}", Ct)).ReadAsync<FlightDetails>(HttpStatusCode.OK);
        flight.FirmwareVersion.Should().Be("PX4 git fd483321");
        flight.StartedAt.Should().BeNull("the log has no GPS time, so the start time is unknown rather than guessed");
    }

    [Fact]
    public async Task Abandoned_processing_is_retried_and_failed_when_retries_are_used_up()
    {
        var client = await factory.CreateUserClientAsync();
        var (_, project) = await client.CreateTeamAndProjectAsync();
        var upload = await (await client.UploadLogAsync(project.Id, Fixture("px4/sample_appended_multiple.ulg"), "crashed.ulg")).ReadAsync<UploadResult>(HttpStatusCode.Created);
        await ProcessQueueAsync(except: upload.LogFile.Id);
        await ClaimAsync(upload.LogFile.Id);

        // The worker died an hour ago: the file is queued again for an automatic retry.
        await RecoverAsAbandonedAsync(upload.LogFile.Id);
        var file = await GetFileAsync(client, upload.LogFile.Id);
        file.Status.Should().Be("Queued");
        var jobs = await (await client.GetAsync($"/api/v1/log-files/{file.Id}/jobs", Ct)).ReadAsync<Job[]>(HttpStatusCode.OK);
        jobs.Should().ContainSingle().Which.FailureCode.Should().Be("processing.interrupted");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var stored = await scope.ServiceProvider.GetRequiredService<FlightLogDbContext>().FlightLogFiles.AsNoTracking().SingleAsync(f => f.Id == file.Id, Ct);
            stored.RetryCount.Should().Be(1);
            stored.NextAttemptAt.Should().BeAfter(DateTimeOffset.UtcNow, "the retry waits for its backoff delay");
        }

        // Once the retries are used up the file fails for good.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<FlightLogDbContext>().FlightLogFiles.Where(f => f.Id == file.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(f => f.RetryCount, 3).SetProperty(f => f.NextAttemptAt, (DateTimeOffset?)null), Ct);
        }

        await ProcessQueueAsync(except: file.Id);
        await ClaimAsync(file.Id);
        await RecoverAsAbandonedAsync(file.Id);
        file = await GetFileAsync(client, file.Id);
        file.Status.Should().Be("Failed");
        file.FailureCode.Should().Be("processing.interrupted");
    }

    private async Task RecoverAsAbandonedAsync(Guid fileId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FlightLogDbContext>();
        await db.FlightLogFiles.Where(f => f.Id == fileId)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.StatusChangedAt, DateTimeOffset.UtcNow.AddHours(-1)), Ct);
        (await scope.ServiceProvider.GetRequiredService<ILogProcessingService>().RecoverStaleAsync(Ct)).Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Deleting_a_flight_removes_its_signal_chunks()
    {
        var client = await factory.CreateUserClientAsync();
        var (_, project) = await client.CreateTeamAndProjectAsync();
        var upload = await (await client.UploadLogAsync(project.Id, Fixtures.Px4Sample, "delete-me.ulg")).ReadAsync<UploadResult>(HttpStatusCode.Created);
        await ProcessQueueAsync();

        string key;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlightLogDbContext>();
            var series = await db.FlightSignalSeries.FirstAsync(s => s.FlightId == upload.Flight.Id, Ct);
            key = series.ChunkKey(0);
        }

        var storage = factory.Services.GetRequiredService<IFileStorage>();
        (await storage.ExistsAsync(key, Ct)).Should().BeTrue();

        (await client.DeleteAsync($"/api/v1/flights/{upload.Flight.Id}", Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await storage.ExistsAsync(key, Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task Flight_data_is_visible_to_team_members_only_and_requests_are_validated()
    {
        var owner = await factory.CreateUserClientAsync();
        var (team, project) = await owner.CreateTeamAndProjectAsync();
        var upload = await (await owner.UploadLogAsync(project.Id, Fixtures.Px4Sample, "team-only.ulg")).ReadAsync<UploadResult>(HttpStatusCode.Created);
        await ProcessQueueAsync();
        var flightId = upload.Flight.Id;

        var viewerEmail = $"viewer-{Guid.NewGuid():N}@flightlog.test";
        var viewer = await factory.CreateUserClientAsync(viewerEmail);
        await owner.AddMemberAsync(team.Id, viewerEmail, "Viewer");
        (await viewer.GetAsync($"/api/v1/flights/{flightId}/signals", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        var outsider = await factory.CreateUserClientAsync();
        foreach (var url in new[]
        {
            $"/api/v1/flights/{flightId}/signals",
            $"/api/v1/flights/{flightId}/signals/data?names=attitude.roll",
            $"/api/v1/flights/{flightId}/events",
            $"/api/v1/flights/{flightId}/parameters",
            $"/api/v1/log-files/{upload.LogFile.Id}/jobs",
        })
        {
            (await outsider.GetAsync(url, Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound, url);
        }

        (await owner.GetAsync($"/api/v1/flights/{flightId}/signals/data", Ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var tooMany = string.Join("&", Enumerable.Range(0, 9).Select(i => $"names=s{i}"));
        (await owner.GetAsync($"/api/v1/flights/{flightId}/signals/data?{tooMany}", Ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await owner.GetAsync($"/api/v1/flights/{flightId}/signals/data?names=no.such_signal", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await owner.GetAsync($"/api/v1/flights/{flightId}/signals/data?names=attitude.roll&fromUs=10&toUs=5", Ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private static async Task<SignalData> GetDataAsync(HttpClient client, Guid flightId, string query) =>
        await (await client.GetAsync($"/api/v1/flights/{flightId}/signals/data?{query}", Ct)).ReadAsync<SignalData>(HttpStatusCode.OK);

    private static async Task<LogFile> GetFileAsync(HttpClient client, Guid id) =>
        await (await client.GetAsync($"/api/v1/log-files/{id}", Ct)).ReadAsync<LogFile>(HttpStatusCode.OK);

    /// <summary>Runs the worker loop until the queue is empty, optionally leaving one file queued.</summary>
    private Task ProcessQueueAsync(Guid? except = null) => factory.ProcessQueueAsync(except);

    private async Task<ClaimedJob> ClaimAsync(Guid fileId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<ILogProcessingService>().ClaimNextAsync("test-worker", Ct);
        result!.Job.Should().NotBeNull();
        result.Job!.LogFileId.Should().Be(fileId);
        return result.Job;
    }

    private async Task ProcessAsync(ClaimedJob claim, CancellationToken cancellation, CancellationToken shutdown)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ILogProcessingService>().ProcessAsync(claim, cancellation, shutdown);
    }
}

internal sealed record FlightDetails(
    Guid Id, string? FirmwareVersion, string? VehicleType, string? Hardware, DateTimeOffset? StartedAt, DateTimeOffset OccurredAt,
    double? DurationSeconds, DateTimeOffset? BootTimeUtc, long? LogStartTimestampUs, Dictionary<string, string> LogMetadata, LogFile? LogFile);

internal sealed record SignalSource(string Message, int Instance, string Fields, string Conversion, string NormalizationVersion);

internal sealed record Signal(
    string Name, string Kind, string Message, int Instance, string Field, string? Unit, double? Multiplier, string SourceType,
    SignalSource? Source, long SampleCount, long InvalidCount, double? Min, double? Max, long? FirstTimestampUs, long? LastTimestampUs);

internal sealed record SignalSeriesData(string Name, string? Unit, long SamplesInRange, bool Downsampled, List<long> TimestampsUs, List<double?> Values);

internal sealed record SignalData(Guid FlightId, long? LogStartTimestampUs, DateTimeOffset? BootTimeUtc, List<SignalSeriesData> Signals);

internal sealed record FlightEvent(long TimestampUs, string Kind, string? Severity, string Source, string Text, Dictionary<string, string>? Data);

internal sealed record Parameter(string Name, double? InitialValue, double? DefaultValue, ParameterChange[] Changes);

internal sealed record ParameterChange(long TimestampUs, double Value);

internal sealed record Job(
    Guid Id, int Attempt, string Outcome, string? ParserName, long RecordCount, int SignalCount, int ParameterCount, long ChunkBytes,
    bool Truncated, long? DurationMs, string? FailureCode, string? FailureMessage);
