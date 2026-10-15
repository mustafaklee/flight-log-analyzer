using System.Net;
using System.Net.Http.Json;
using FlightLog.Application.Abstractions;
using FlightLog.Application.Processing;
using FlightLog.Infrastructure.Storage;
using FlightLog.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FlightLog.IntegrationTests;

/// <summary>The automatic analysis after processing, manual runs, redelivery and the analysis endpoints, on real logs.</summary>
public sealed class AnalysisTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Fixture(string path) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", path));

    [Fact]
    public async Task Ardupilot_log_is_analyzed_automatically_after_processing()
    {
        var client = await factory.CreateUserClientAsync();
        var (_, project) = await client.CreateTeamAndProjectAsync();
        var upload = await (await client.UploadLogAsync(project.Id, Fixture("ardupilot/sitl-copter-4.6.3.bin"), "sitl.bin")).ReadAsync<UploadResult>(HttpStatusCode.Created);

        await factory.ProcessQueueAsync();

        var runs = await (await client.GetAsync($"/api/v1/flights/{upload.Flight.Id}/analysis-runs", Ct)).ReadAsync<AnalysisRun[]>(HttpStatusCode.OK);
        var summary = runs.Should().ContainSingle().Which;
        summary.Status.Should().Be("Succeeded");
        summary.Trigger.Should().Be("Automatic");
        summary.EngineVersion.Should().Be("rules-1");
        summary.Rules.Should().BeNull("the list does not carry rule results");

        var run = await (await client.GetAsync($"/api/v1/analysis-runs/{summary.Id}", Ct)).ReadAsync<AnalysisRun>(HttpStatusCode.OK);
        run.Rules.Should().HaveCount(13).And.OnlyContain(r => r.Status != "Failed");
        run.RulesApplied.Should().Be(run.Rules!.Count(r => r.Status == "Applied"));
        run.Rules.Single(r => r.RuleId == "vibration.level").Status.Should().Be("Applied", "VIBE is logged by ArduPilot");
        run.Rules.Single(r => r.RuleId == "battery.remaining").Reason.Should().Contain("No low-battery level", "SITL sets no BATT_LOW_MAH");
        run.InputSummary.Should().Contain("parser", "dataflash 1.0.0").And.Contain("armedPeriods", "1");

        var findings = await GetFindingsAsync(client, run.Id);
        findings.TotalCount.Should().Be(run.FindingCount);
        var gap = findings.Items.Should().ContainSingle(f => f.RuleId == "data.gaps").Which;
        gap.StartUs.Should().Be(38_409_630);
        gap.EndUs.Should().Be(48_809_635);
        gap.Severity.Should().Be("Warning");
        gap.Measured["longestGapS"].Should().BeApproximately(10.4, 0.01);
        gap.ThresholdSource.Should().NotBeNullOrWhiteSpace();
        findings.Items.Should().Contain(f => f.RuleId == "modes.transitions" && f.Title == "RTL mode engaged" && f.StartUs == 90_772_010);
        findings.Items.Should().NotContain(f => f.RuleId == "parameters.changes", "STAT_* counters are bookkeeping, not a configuration change");
        findings.Items.Should().BeInAscendingOrder(f => f.StartUs);

        var warnings = await GetFindingsAsync(client, run.Id, "minSeverity=Warning");
        warnings.Items.Should().NotBeEmpty().And.OnlyContain(f => f.Severity != "Info");
        var byRule = await GetFindingsAsync(client, run.Id, "ruleId=modes.transitions");
        byRule.Items.Should().OnlyContain(f => f.RuleId == "modes.transitions");
        (await client.GetAsync($"/api/v1/analysis-runs/{run.Id}/findings?pageSize=500", Ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var flight = await (await client.GetAsync($"/api/v1/flights/{upload.Flight.Id}", Ct)).ReadAsync<FlightWithAnalysis>(HttpStatusCode.OK);
        flight.LatestAnalysis!.RunId.Should().Be(run.Id);
        flight.LatestAnalysis.FindingCount.Should().Be(run.FindingCount);
        flight.LatestAnalysis.HighestSeverity.Should().Be("Warning");
        var list = await (await client.GetAsync($"/api/v1/flights?projectId={project.Id}", Ct)).ReadAsync<Paged<FlightWithAnalysis>>(HttpStatusCode.OK);
        list.Items.Single().LatestAnalysis!.RunId.Should().Be(run.Id);
    }

    [Fact]
    public async Task Px4_log_with_a_mode_change_is_analyzed()
    {
        var client = await factory.CreateUserClientAsync();
        var (_, project) = await client.CreateTeamAndProjectAsync();
        var upload = await (await client.UploadLogAsync(project.Id, Fixture("px4/sample_px4_events.ulg"), "events.ulg")).ReadAsync<UploadResult>(HttpStatusCode.Created);

        await factory.ProcessQueueAsync();

        var run = (await (await client.GetAsync($"/api/v1/flights/{upload.Flight.Id}/analysis-runs", Ct)).ReadAsync<AnalysisRun[]>(HttpStatusCode.OK)).Single();
        run.Status.Should().Be("Succeeded");
        var details = await (await client.GetAsync($"/api/v1/analysis-runs/{run.Id}", Ct)).ReadAsync<AnalysisRun>(HttpStatusCode.OK);
        details.Rules!.Single(r => r.RuleId == "vibration.level").Status.Should().Be("NotApplicable", "PX4 logs one combined metric, not per-axis values");
        details.Rules!.Single(r => r.RuleId == "ekf.innovation").Status.Should().Be("Applied", "estimator_innovation_test_ratios is logged");
        var findings = await GetFindingsAsync(client, run.Id);
        findings.Items.Should().ContainSingle(f => f.Title == "AUTO_RTL mode engaged");
    }

    [Fact]
    public async Task Manual_rerun_is_queued_once_and_produces_the_same_findings()
    {
        var client = await factory.CreateUserClientAsync();
        var (_, project) = await client.CreateTeamAndProjectAsync();
        var upload = await (await client.UploadLogAsync(project.Id, Fixture("px4/sample.ulg"), "sample.ulg")).ReadAsync<UploadResult>(HttpStatusCode.Created);
        await factory.ProcessQueueAsync();
        var first = (await (await client.GetAsync($"/api/v1/flights/{upload.Flight.Id}/analysis-runs", Ct)).ReadAsync<AnalysisRun[]>(HttpStatusCode.OK)).Single();

        var response = await client.PostAsync($"/api/v1/flights/{upload.Flight.Id}/analysis-runs", null, Ct);
        var queued = await response.ReadAsync<AnalysisRun>(HttpStatusCode.Accepted);
        response.Headers.Location!.ToString().Should().EndWith($"/api/v1/analysis-runs/{queued.Id}");
        queued.Status.Should().Be("Queued");
        queued.Trigger.Should().Be("Manual");
        queued.RequestedBy.Should().Be(await client.CurrentUserIdAsync());
        (await GetFileAsync(client, upload.LogFile.Id)).Status.Should().Be("AnalysisPending");

        var again = await client.PostAsync($"/api/v1/flights/{upload.Flight.Id}/analysis-runs", null, Ct);
        again.StatusCode.Should().Be(HttpStatusCode.Conflict);

        await factory.ProcessQueueAsync();

        var runs = await (await client.GetAsync($"/api/v1/flights/{upload.Flight.Id}/analysis-runs", Ct)).ReadAsync<AnalysisRun[]>(HttpStatusCode.OK);
        runs.Select(r => r.Id).Should().Equal(queued.Id, first.Id);
        runs[0].Status.Should().Be("Succeeded");
        (await GetFileAsync(client, upload.LogFile.Id)).Status.Should().Be("Completed");

        var before = await GetFindingsAsync(client, first.Id);
        var after = await GetFindingsAsync(client, queued.Id);
        after.Items.Select(Key).Should().Equal(before.Items.Select(Key), "the analysis is deterministic");
        after.Items.Should().NotBeEmpty();

        static string Key(Finding f) => $"{f.RuleId}|{f.Title}|{f.StartUs}|{f.EndUs}|{f.Severity}|{string.Join(',', f.Measured.OrderBy(m => m.Key))}";
    }

    [Fact]
    public async Task Redelivered_messages_do_not_repeat_work()
    {
        var client = await factory.CreateUserClientAsync();
        var (_, project) = await client.CreateTeamAndProjectAsync();
        var upload = await (await client.UploadLogAsync(project.Id, Fixture("px4/sample_log_small.ulg"), "small.ulg")).ReadAsync<UploadResult>(HttpStatusCode.Created);
        await factory.ProcessQueueAsync();
        var run = (await (await client.GetAsync($"/api/v1/flights/{upload.Flight.Id}/analysis-runs", Ct)).ReadAsync<AnalysisRun[]>(HttpStatusCode.OK)).Single();

        var runner = factory.Services.GetRequiredService<WorkRunner>();
        (await runner.ProcessFileAsync(upload.LogFile.Id, "duplicate", Ct)).Should().Be(WorkOutcome.Skipped);
        (await runner.AnalyzeAsync(run.Id, "duplicate", Ct)).Should().Be(WorkOutcome.Skipped);
        (await runner.ProcessFileAsync(Guid.NewGuid(), "unknown", Ct)).Should().Be(WorkOutcome.Skipped);
        (await runner.AnalyzeAsync(Guid.NewGuid(), "unknown", Ct)).Should().Be(WorkOutcome.Skipped);

        var jobs = await (await client.GetAsync($"/api/v1/log-files/{upload.LogFile.Id}/jobs", Ct)).ReadAsync<Job[]>(HttpStatusCode.OK);
        jobs.Should().ContainSingle();
        var unchanged = await (await client.GetAsync($"/api/v1/analysis-runs/{run.Id}", Ct)).ReadAsync<AnalysisRun>(HttpStatusCode.OK);
        unchanged.Attempt.Should().Be(run.Attempt);
        unchanged.FindingCount.Should().Be(run.FindingCount);
    }

    [Fact]
    public async Task Rules_are_listed_with_their_requirements()
    {
        var client = await factory.CreateUserClientAsync();

        var rules = await (await client.GetAsync("/api/v1/analysis-rules", Ct)).ReadAsync<Rule[]>(HttpStatusCode.OK);

        rules.Should().HaveCount(13);
        rules.Select(r => r.Id).Should().OnlyHaveUniqueItems().And.Contain(["gps.fix_loss", "ekf.innovation", "vibration.level", "battery.voltage", "events.failsafe", "data.gaps"]);
        rules.Should().OnlyContain(r => r.Version >= 1 && r.RequiredData.Length > 0 && r.Applicability.Length > 0);
    }

    [Fact]
    public async Task Analysis_follows_team_roles()
    {
        var owner = await factory.CreateUserClientAsync();
        var (team, project) = await owner.CreateTeamAndProjectAsync();
        var upload = await (await owner.UploadLogAsync(project.Id, Fixture("px4/sample_log_small.ulg"), "small.ulg")).ReadAsync<UploadResult>(HttpStatusCode.Created);
        await factory.ProcessQueueAsync();
        var run = (await (await owner.GetAsync($"/api/v1/flights/{upload.Flight.Id}/analysis-runs", Ct)).ReadAsync<AnalysisRun[]>(HttpStatusCode.OK)).Single();

        var outsider = await factory.CreateUserClientAsync();
        (await outsider.GetAsync($"/api/v1/flights/{upload.Flight.Id}/analysis-runs", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await outsider.GetAsync($"/api/v1/analysis-runs/{run.Id}", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await outsider.GetAsync($"/api/v1/analysis-runs/{run.Id}/findings", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await outsider.PostAsync($"/api/v1/flights/{upload.Flight.Id}/analysis-runs", null, Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var viewerEmail = $"viewer-{Guid.NewGuid():N}@example.com";
        var viewer = await factory.CreateUserClientAsync(viewerEmail);
        await owner.AddMemberAsync(team.Id, viewerEmail, "Viewer");
        (await viewer.GetAsync($"/api/v1/analysis-runs/{run.Id}/findings", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await viewer.PostAsync($"/api/v1/flights/{upload.Flight.Id}/analysis-runs", null, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await owner.GetAsync($"/api/v1/analysis-runs/{Guid.NewGuid()}", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Processing_statistics_are_for_administrators()
    {
        var client = await factory.CreateUserClientAsync();
        var (_, project) = await client.CreateTeamAndProjectAsync();
        await client.UploadLogAsync(project.Id, Fixture("px4/sample_log_small.ulg"), "small.ulg");
        await factory.ProcessQueueAsync();

        (await client.GetAsync("/api/v1/admin/processing/stats", Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var admin = await factory.CreateAuthenticatedClientAsync(ApiFactory.AdminEmail, ApiFactory.AdminPassword);
        var stats = await (await admin.GetAsync("/api/v1/admin/processing/stats?hours=1", Ct)).ReadAsync<Stats>(HttpStatusCode.OK);
        stats.FilesByStatus.Should().ContainKey("Completed");
        stats.JobsByOutcome.Should().ContainKey("Succeeded");
        stats.AnalysisRunsByStatus.Should().ContainKey("Succeeded");
        stats.JobDuration.Count.Should().BeGreaterThan(0);
        stats.OutboxPending.Should().Be(0, "the database transport writes no outbox rows");
        (await admin.GetAsync("/api/v1/admin/processing/stats?hours=0", Ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private static async Task<Paged<Finding>> GetFindingsAsync(HttpClient client, Guid runId, string? query = null) =>
        await (await client.GetAsync($"/api/v1/analysis-runs/{runId}/findings?pageSize=200&{query}", Ct)).ReadAsync<Paged<Finding>>(HttpStatusCode.OK);

    private static async Task<LogFile> GetFileAsync(HttpClient client, Guid id) =>
        await (await client.GetAsync($"/api/v1/log-files/{id}", Ct)).ReadAsync<LogFile>(HttpStatusCode.OK);
}

/// <summary>Temporary object storage failures are retried automatically, with a limit.</summary>
public sealed class TransientFailureTests(TransientFailureTests.FlakyStorageFactory factory) : IClassFixture<TransientFailureTests.FlakyStorageFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_temporary_storage_failure_is_retried_and_the_file_completes()
    {
        var client = await factory.CreateUserClientAsync();
        var (_, project) = await client.CreateTeamAndProjectAsync();
        var upload = await (await client.UploadLogAsync(project.Id, Fixtures.Px4Sample, "sample.ulg")).ReadAsync<UploadResult>(HttpStatusCode.Created);

        factory.Storage.FailNextReads(1);
        await factory.ProcessQueueAsync();

        var jobs = await (await client.GetAsync($"/api/v1/log-files/{upload.LogFile.Id}/jobs", Ct)).ReadAsync<Job[]>(HttpStatusCode.OK);
        jobs.Select(j => j.Outcome).Should().BeEquivalentTo(["Failed", "Succeeded"]);
        jobs.Single(j => j.Outcome == "Failed").FailureCode.Should().Be("processing.transient_failure");
        (await client.GetFromJsonAsync<LogFile>($"/api/v1/log-files/{upload.LogFile.Id}", ApiClientExtensions.Json, Ct))!.Status.Should().Be("Completed");
    }

    [Fact]
    public async Task Retries_stop_after_the_limit()
    {
        var client = await factory.CreateUserClientAsync();
        var (_, project) = await client.CreateTeamAndProjectAsync();
        var upload = await (await client.UploadLogAsync(project.Id, Fixtures.Px4Sample, "sample.ulg")).ReadAsync<UploadResult>(HttpStatusCode.Created);

        factory.Storage.FailNextReads(int.MaxValue);
        try
        {
            await factory.ProcessQueueAsync();
        }
        finally
        {
            factory.Storage.FailNextReads(0);
        }

        var file = (await client.GetFromJsonAsync<LogFile>($"/api/v1/log-files/{upload.LogFile.Id}", ApiClientExtensions.Json, Ct))!;
        file.Status.Should().Be("Failed");
        file.FailureCode.Should().Be("processing.retries_exhausted");
        var jobs = await (await client.GetAsync($"/api/v1/log-files/{upload.LogFile.Id}/jobs", Ct)).ReadAsync<Job[]>(HttpStatusCode.OK);
        jobs.Should().HaveCount(4, "one attempt and three automatic retries").And.OnlyContain(j => j.Outcome == "Failed");

        // A manual retry starts over.
        (await client.PostAsync($"/api/v1/log-files/{upload.LogFile.Id}/retry", null, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        await factory.ProcessQueueAsync();
        (await client.GetFromJsonAsync<LogFile>($"/api/v1/log-files/{upload.LogFile.Id}", ApiClientExtensions.Json, Ct))!.Status.Should().Be("Completed");
    }

    public sealed class FlakyStorageFactory : ApiFactory
    {
        public FlakyStorage Storage { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Processing:RetryBaseDelay", "00:00:00");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IFileStorage>();
                services.AddSingleton<IFileStorage>(sp =>
                {
                    Storage.Inner = sp.GetRequiredService<S3FileStorage>();
                    return Storage;
                });
            });
        }
    }

    /// <summary>Object storage whose next reads fail as if the storage service were unreachable.</summary>
    public sealed class FlakyStorage : IFileStorage
    {
        private int _failures;

        public IFileStorage Inner { get; set; } = null!;

        public void FailNextReads(int count) => Volatile.Write(ref _failures, count);

        public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken) => Inner.ExistsAsync(key, cancellationToken);

        public Task UploadFileAsync(string key, string localPath, string contentType, CancellationToken cancellationToken) =>
            Inner.UploadFileAsync(key, localPath, contentType, cancellationToken);

        public Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _failures) > 0)
            {
                Interlocked.Decrement(ref _failures);
                throw new HttpRequestException("Simulated storage outage.");
            }

            return Inner.OpenReadAsync(key, cancellationToken);
        }

        public Task UploadBytesAsync(string key, ReadOnlyMemory<byte> content, string contentType, CancellationToken cancellationToken) =>
            Inner.UploadBytesAsync(key, content, contentType, cancellationToken);

        public Task<byte[]> ReadAllBytesAsync(string key, CancellationToken cancellationToken) => Inner.ReadAllBytesAsync(key, cancellationToken);

        public Task DeleteAsync(string key, CancellationToken cancellationToken) => Inner.DeleteAsync(key, cancellationToken);

        public Task<int> DeletePrefixAsync(string prefix, CancellationToken cancellationToken) => Inner.DeletePrefixAsync(prefix, cancellationToken);
    }
}

internal sealed record AnalysisRuleResult(string RuleId, int RuleVersion, string Status, string? Reason, int FindingCount, string[] Signals, long DurationMs);

internal sealed record AnalysisRun(
    Guid Id, Guid FlightId, Guid LogFileId, string Kind, string Trigger, string Status, string EngineVersion, Guid? RequestedBy, int Attempt,
    int RetryCount, DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, long? DurationMs, int FindingCount,
    int RulesApplied, int RulesNotApplicable, int RulesFailed, Dictionary<string, string> InputSummary, string? FailureCode, AnalysisRuleResult[]? Rules);

internal sealed record Finding(
    Guid Id, string RuleId, int RuleVersion, string Category, string Title, string Description, string Severity, string Confidence, long StartUs,
    long EndUs, string[] Signals, Dictionary<string, double> Measured, Dictionary<string, double> Threshold, string ThresholdSource,
    double? DataCoverage, int OccurrenceCount, string? Limitations);

internal sealed record Rule(string Id, int Version, string Category, string Title, string Description, string[] RequiredData, string Applicability);

internal sealed record AnalysisSummary(Guid RunId, int FindingCount, string? HighestSeverity);

internal sealed record FlightWithAnalysis(Guid Id, AnalysisSummary? LatestAnalysis);

internal sealed record DurationSummary(int Count, double? AverageMs, double? P95Ms, double? MaxMs);

internal sealed record Stats(
    Dictionary<string, int> FilesByStatus, Dictionary<string, int> JobsByOutcome, Dictionary<string, int> AnalysisRunsByStatus, DurationSummary JobDuration,
    int OutboxPending);
