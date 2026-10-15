using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FlightLog.Application.Ai;
using FlightLog.Domain.Analysis;
using FlightLog.Infrastructure.Persistence;
using FlightLog.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FlightLog.IntegrationTests;

/// <summary>
/// AI reports end to end on a real log: request, worker, context, validation, storage, export, quota and every
/// failure path. The model is replaced by <see cref="ScriptedProvider"/>; the Claude adapter itself is covered
/// against a stubbed HTTP endpoint in the unit tests.
/// </summary>
public sealed partial class AiAnalysisTests(AiAnalysisTests.AiFactory factory) : IClassFixture<AiAnalysisTests.AiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly byte[] SitlLog = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "ardupilot", "sitl-copter-4.6.3.bin"));

    private ScriptedProvider Model => factory.Provider;

    [Fact]
    public async Task A_report_cites_real_measurements_and_marks_what_it_cannot_support()
    {
        Model.Reset();
        var (client, flightId) = await ProcessedFlightAsync();

        var status = await (await client.GetAsync($"/api/v1/teams/{await TeamOfAsync(client, flightId)}/ai-status", Ct)).ReadAsync<AiStatusDto>(HttpStatusCode.OK);
        status.Should().BeEquivalentTo(new AiStatusDto(true, "Anthropic", "claude-opus-5-5", FlightReportPrompt.Version, 3, 0));

        var queued = await (await client.PostAsync($"/api/v1/flights/{flightId}/ai-analyses", null, Ct)).ReadAsync<AiAnalysisDto>(HttpStatusCode.Accepted);
        queued.Status.Should().Be(AiAnalysisStatus.Queued);
        var again = await (await client.PostAsync($"/api/v1/flights/{flightId}/ai-analyses", null, Ct)).ReadAsync<Problem>(HttpStatusCode.Conflict);
        again.Code.Should().Be("ai.already_pending");

        await factory.ProcessQueueAsync();

        var analysis = await (await client.GetAsync($"/api/v1/ai-analyses/{queued.Id}", Ct)).ReadAsync<AiAnalysisDto>(HttpStatusCode.OK);
        analysis.Status.Should().Be(AiAnalysisStatus.Succeeded, analysis.FailureMessage);
        analysis.PromptVersion.Should().Be(FlightReportPrompt.Version);
        analysis.ContextVersion.Should().Be(FlightContextBuilder.Version);
        analysis.ProviderCalls.Should().Be(1);
        analysis.InputTokens.Should().Be(2000);
        analysis.OutputTokens.Should().Be(500);
        analysis.CostUsd.Should().Be(0.018m, "2000 input tokens at $4/M plus 500 output tokens at $20/M");
        analysis.StopReason.Should().Be("end_turn");
        analysis.LogStartUs.Should().NotBeNull();

        var report = analysis.Report!;
        report.Items.Should().HaveCount(2);
        var supported = report.Items[0];
        supported.Supported.Should().BeTrue();
        supported.Evidence.Should().ContainSingle().Which.Kind.Should().Be(EvidenceKind.Finding);
        supported.Evidence[0].FindingId.Should().NotBeNull();
        supported.UnverifiedNumbers.Should().BeEmpty("the number comes from the finding's measurement");
        var invented = report.Items[1];
        invented.Supported.Should().BeFalse();
        invented.UnknownRefs.Should().Equal("F99");
        invented.UnverifiedNumbers.Should().Equal("987.6");
        report.Validation.UnsupportedItems.Should().Be(1);

        // What the model saw: the stored context, its hash, and no position data.
        var context = await (await client.GetAsync($"/api/v1/ai-analyses/{queued.Id}/context", Ct)).Content.ReadAsStringAsync(Ct);
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(context))).Should().Be(analysis.ContextHash);
        context.Should().Contain("\"contextVersion\":\"flight-context-v1\"").And.NotContain("gps.lat").And.NotContain("gps.lon");
        Model.Requests.Should().ContainSingle().Which.UserMessage.Should().Contain(context);
        Model.Requests[0].SystemPrompt.Should().Be(FlightReportPrompt.SystemPrompt);
        Model.Requests[0].MaxOutputTokens.Should().Be(16_000);

        var markdown = await client.GetAsync($"/api/v1/ai-analyses/{queued.Id}/report.md", Ct);
        markdown.StatusCode.Should().Be(HttpStatusCode.OK);
        markdown.Content.Headers.ContentType!.MediaType.Should().Be("text/markdown");
        markdown.Content.Headers.ContentDisposition!.FileNameStar.Should().StartWith("ai-rapor-");
        var text = await markdown.Content.ReadAsStringAsync(Ct);
        text.Should().Contain("## Desteklenmeyen iddialar").And.Contain("Bağlamda bulunmayan sayılar: 987.6").And.Contain(analysis.ContextHash![..12]);

        var list = await (await client.GetAsync($"/api/v1/flights/{flightId}/ai-analyses", Ct)).ReadAsync<AiAnalysisDto[]>(HttpStatusCode.OK);
        list.Should().ContainSingle().Which.Report.Should().BeNull("the list does not carry report bodies");
    }

    [Fact]
    public async Task A_rate_limited_call_is_retried_later_and_its_cost_is_kept()
    {
        Model.Reset();
        var (client, flightId) = await ProcessedFlightAsync();
        Model.Next(_ => throw new AiProviderException(AiProviderErrorKind.RateLimited, "429", TimeSpan.FromMinutes(5)));

        var queued = await (await client.PostAsync($"/api/v1/flights/{flightId}/ai-analyses", null, Ct)).ReadAsync<AiAnalysisDto>(HttpStatusCode.Accepted);
        await factory.ProcessQueueAsync();

        var waiting = await GetAsync(client, queued.Id);
        waiting.Status.Should().Be(AiAnalysisStatus.Queued);
        waiting.RetryCount.Should().Be(1);
        waiting.ProviderCalls.Should().Be(1, "the failed call is recorded");
        waiting.NextAttemptAt.Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(4), "the provider's retry-after is longer than the backoff");

        await MakeDueAsync(queued.Id);
        await factory.ProcessQueueAsync();

        var done = await GetAsync(client, queued.Id);
        done.Status.Should().Be(AiAnalysisStatus.Succeeded);
        done.Attempt.Should().Be(2);
        done.ProviderCalls.Should().Be(2);
    }

    [Fact]
    public async Task Retries_end_when_the_provider_stays_unavailable()
    {
        Model.Reset();
        var (client, flightId) = await ProcessedFlightAsync();
        Model.Always(_ => throw new AiProviderException(AiProviderErrorKind.Transient, "overloaded"));

        var queued = await (await client.PostAsync($"/api/v1/flights/{flightId}/ai-analyses", null, Ct)).ReadAsync<AiAnalysisDto>(HttpStatusCode.Accepted);
        for (var i = 0; i < 3; i++)
        {
            await factory.ProcessQueueAsync();
            await MakeDueAsync(queued.Id);
        }

        var failed = await GetAsync(client, queued.Id);
        failed.Status.Should().Be(AiAnalysisStatus.Failed);
        failed.FailureCode.Should().Be("ai.provider_unavailable");
        failed.FailureMessage.Should().Contain("Gave up after 2 automatic retries");
        failed.ProviderCalls.Should().Be(3);
    }

    [Theory]
    [InlineData("refusal", "ai.refused")]
    [InlineData("max_tokens", "ai.truncated")]
    public async Task A_refused_or_cut_off_answer_fails_without_report(string stopReason, string code)
    {
        Model.Reset();
        var (client, flightId) = await ProcessedFlightAsync();
        Model.Next(_ => new AiProviderResponse("{\"summary\":", stopReason, "claude-opus-5-5", new AiUsage(2000, 100, 0, 0), 10, "declined"));

        var queued = await (await client.PostAsync($"/api/v1/flights/{flightId}/ai-analyses", null, Ct)).ReadAsync<AiAnalysisDto>(HttpStatusCode.Accepted);
        await factory.ProcessQueueAsync();

        var failed = await GetAsync(client, queued.Id);
        failed.Status.Should().Be(AiAnalysisStatus.Failed);
        failed.FailureCode.Should().Be(code);
        failed.Report.Should().BeNull();
        failed.InputTokens.Should().Be(2000, "a failed call is still paid for");
        (await client.GetAsync($"/api/v1/ai-analyses/{queued.Id}/report.md", Ct)).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task An_unreadable_answer_gets_one_repair_call()
    {
        Model.Reset();
        var (client, flightId) = await ProcessedFlightAsync();
        Model.Next(_ => new AiProviderResponse("not json", "end_turn", "claude-opus-5-5", new AiUsage(100, 10, 0, 0), 10));

        var queued = await (await client.PostAsync($"/api/v1/flights/{flightId}/ai-analyses", null, Ct)).ReadAsync<AiAnalysisDto>(HttpStatusCode.Accepted);
        await factory.ProcessQueueAsync();

        var repaired = await GetAsync(client, queued.Id);
        repaired.Status.Should().Be(AiAnalysisStatus.Succeeded);
        repaired.ProviderCalls.Should().Be(2);
        Model.Requests[1].UserMessage.Should().Contain("A previous answer could not be used");

        Model.Reset();
        Model.Always(_ => new AiProviderResponse("not json", "end_turn", "claude-opus-5-5", new AiUsage(100, 10, 0, 0), 10));
        var second = await (await client.PostAsync($"/api/v1/flights/{flightId}/ai-analyses", null, Ct)).ReadAsync<AiAnalysisDto>(HttpStatusCode.Accepted);
        await factory.ProcessQueueAsync();

        var failed = await GetAsync(client, second.Id);
        failed.Status.Should().Be(AiAnalysisStatus.Failed);
        failed.FailureCode.Should().Be("ai.invalid_output");
        failed.ProviderCalls.Should().Be(2);
    }

    [Fact]
    public async Task A_rejected_key_fails_at_once()
    {
        Model.Reset();
        var (client, flightId) = await ProcessedFlightAsync();
        Model.Next(_ => throw new AiProviderException(AiProviderErrorKind.Authentication, "401"));

        var queued = await (await client.PostAsync($"/api/v1/flights/{flightId}/ai-analyses", null, Ct)).ReadAsync<AiAnalysisDto>(HttpStatusCode.Accepted);
        await factory.ProcessQueueAsync();

        var failed = await GetAsync(client, queued.Id);
        failed.FailureCode.Should().Be("ai.provider_auth");
        failed.RetryCount.Should().Be(0);
    }

    [Fact]
    public async Task A_worker_without_key_fails_the_analysis_clearly()
    {
        Model.Reset();
        var (client, flightId) = await ProcessedFlightAsync();
        Model.Configured = false;

        var queued = await (await client.PostAsync($"/api/v1/flights/{flightId}/ai-analyses", null, Ct)).ReadAsync<AiAnalysisDto>(HttpStatusCode.Accepted);
        await factory.ProcessQueueAsync();

        (await GetAsync(client, queued.Id)).FailureCode.Should().Be("ai.not_configured");
        Model.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task The_daily_quota_and_the_rules_of_access_are_enforced()
    {
        Model.Reset();
        var owner = await factory.CreateUserClientAsync();
        var (team, project) = await owner.CreateTeamAndProjectAsync();
        var upload = await (await owner.UploadLogAsync(project.Id, SitlLog, "sitl.bin")).ReadAsync<UploadResult>(HttpStatusCode.Created);
        var flightId = upload.Flight.Id;

        (await (await owner.PostAsync($"/api/v1/flights/{flightId}/ai-analyses", null, Ct)).ReadAsync<Problem>(HttpStatusCode.UnprocessableEntity))
            .Code.Should().Be("ai.no_rule_analysis", "the log is not processed yet");
        await factory.ProcessQueueAsync();

        var outsider = await factory.CreateUserClientAsync();
        (await outsider.PostAsync($"/api/v1/flights/{flightId}/ai-analyses", null, Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await outsider.GetAsync($"/api/v1/teams/{team.Id}/ai-status", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var viewerEmail = $"viewer-{Guid.NewGuid():N}@example.com";
        var viewer = await factory.CreateUserClientAsync(viewerEmail);
        await owner.AddMemberAsync(team.Id, viewerEmail, "Viewer");
        (await viewer.PostAsync($"/api/v1/flights/{flightId}/ai-analyses", null, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        AiAnalysisDto? last = null;
        for (var i = 0; i < 3; i++)
        {
            last = await (await owner.PostAsync($"/api/v1/flights/{flightId}/ai-analyses", null, Ct)).ReadAsync<AiAnalysisDto>(HttpStatusCode.Accepted);
            await factory.ProcessQueueAsync();
        }

        (await viewer.GetAsync($"/api/v1/ai-analyses/{last!.Id}", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await viewer.GetAsync($"/api/v1/ai-analyses/{last.Id}/context", Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden, "the raw context is for engineers");
        (await outsider.GetAsync($"/api/v1/ai-analyses/{last.Id}", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var limited = await owner.PostAsync($"/api/v1/flights/{flightId}/ai-analyses", null, Ct);
        (await limited.ReadAsync<Problem>(HttpStatusCode.TooManyRequests)).Code.Should().Be("ai.quota_exceeded");
        limited.Headers.RetryAfter!.Delta.Should().BePositive();
        (await (await owner.GetAsync($"/api/v1/teams/{team.Id}/ai-status", Ct)).ReadAsync<AiStatusDto>(HttpStatusCode.OK)).UsedToday.Should().Be(3);
    }

    [Fact]
    public async Task Administrators_see_usage_and_cost()
    {
        Model.Reset();
        var (client, flightId) = await ProcessedFlightAsync();
        await (await client.PostAsync($"/api/v1/flights/{flightId}/ai-analyses", null, Ct)).ReadAsync<AiAnalysisDto>(HttpStatusCode.Accepted);
        await factory.ProcessQueueAsync();

        (await client.GetAsync("/api/v1/admin/ai/usage", Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var admin = await factory.CreateAuthenticatedClientAsync(ApiFactory.AdminEmail, ApiFactory.AdminPassword);
        var usage = await (await admin.GetAsync("/api/v1/admin/ai/usage?days=7", Ct)).ReadAsync<AiUsageDto>(HttpStatusCode.OK);
        usage.Enabled.Should().BeTrue();
        usage.Totals.Succeeded.Should().BePositive();
        usage.Totals.CostUsd.Should().BePositive();
        usage.Totals.AverageLatencyMs.Should().NotBeNull();
        usage.ByModel.Should().Contain(m => m.Model == "claude-opus-5-5");
        usage.ByDay.Should().NotBeEmpty();
        usage.ByTeam.Should().NotBeEmpty();
        (await admin.GetAsync("/api/v1/admin/ai/usage?days=0", Ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private async Task<(HttpClient Client, Guid FlightId)> ProcessedFlightAsync()
    {
        var client = await factory.CreateUserClientAsync();
        var (_, project) = await client.CreateTeamAndProjectAsync();
        var upload = await (await client.UploadLogAsync(project.Id, SitlLog, "sitl.bin")).ReadAsync<UploadResult>(HttpStatusCode.Created);
        await factory.ProcessQueueAsync();
        return (client, upload.Flight.Id);
    }

    private static async Task<Guid> TeamOfAsync(HttpClient client, Guid flightId) =>
        (await (await client.GetAsync($"/api/v1/flights/{flightId}", Ct)).ReadAsync<Flight>(HttpStatusCode.OK)).TeamId;

    private static async Task<AiAnalysisDto> GetAsync(HttpClient client, Guid id) =>
        await (await client.GetAsync($"/api/v1/ai-analyses/{id}", Ct)).ReadAsync<AiAnalysisDto>(HttpStatusCode.OK);

    private async Task MakeDueAsync(Guid id)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FlightLogDbContext>();
        await db.AiAnalyses.Where(a => a.Id == id && a.NextAttemptAt != null).ExecuteUpdateAsync(s => s.SetProperty(a => a.NextAttemptAt, (DateTimeOffset?)null), Ct);
    }

    public sealed class AiFactory : ApiFactory
    {
        public ScriptedProvider Provider { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Ai:Enabled", "true");
            builder.UseSetting("Ai:DailyLimitPerTeam", "3");
            builder.UseSetting("Ai:RetryBaseDelay", "00:00:01");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IFlightAnalysisProvider>();
                services.AddSingleton<IFlightAnalysisProvider>(Provider);
            });
        }
    }

    /// <summary>
    /// Stands in for the model. By default it answers like a model that cites the first finding correctly and
    /// also makes one claim with an invented reference and an invented number.
    /// </summary>
    public sealed partial class ScriptedProvider : IFlightAnalysisProvider
    {
        private readonly Queue<Func<AiProviderRequest, AiProviderResponse>> _next = new();
        private Func<AiProviderRequest, AiProviderResponse>? _always;

        public List<AiProviderRequest> Requests { get; } = [];

        public bool Configured { get; set; } = true;

        public string Name => "Anthropic";

        public string Model => "claude-opus-5-5";

        public bool IsConfigured => Configured;

        public void Reset()
        {
            _next.Clear();
            _always = null;
            Requests.Clear();
            Configured = true;
        }

        public void Next(Func<AiProviderRequest, AiProviderResponse> answer) => _next.Enqueue(answer);

        public void Always(Func<AiProviderRequest, AiProviderResponse> answer) => _always = answer;

        public Task<AiProviderResponse> CompleteAsync(AiProviderRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var answer = _next.Count > 0 ? _next.Dequeue() : _always ?? Report;
            return Task.FromResult(answer(request));
        }

        private static AiProviderResponse Report(AiProviderRequest request)
        {
            var json = ContextPattern().Match(request.UserMessage).Groups[1].Value;
            using var context = JsonDocument.Parse(json);
            var findings = context.RootElement.GetProperty("findings");
            if (findings.GetArrayLength() == 0)
            {
                throw new InvalidOperationException("The scripted report needs a flight with findings.");
            }

            var finding = findings[0];
            var measured = finding.GetProperty("measured").EnumerateObject().First();
            var report = new
            {
                summary = $"{finding.GetProperty("title").GetString()} bulundu.",
                plainSummary = "Uçuşta dikkat edilmesi gereken bir durum var.",
                overallAssessment = "minor_issues",
                items = new object[]
                {
                    Item("Ölçülen değer", $"{measured.Name} değeri {measured.Value.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture)} oldu.", "F1"),
                    Item("Uydurma iddia", "Motor sıcaklığı 987.6 dereceye çıktı.", "F99"),
                },
                followUpQuestions = Array.Empty<string>(),
                limitations = new[] { "Test cevabı." },
            };
            return new AiProviderResponse(JsonSerializer.Serialize(report), "end_turn", "claude-opus-5-5", new AiUsage(2000, 500, 0, 0), 25);
        }

        private static object Item(string title, string statement, string reference) => new
        {
            title,
            kind = "observation",
            statement,
            severity = "info",
            confidence = "low",
            confidenceReason = "Test.",
            evidence = new[] { new { @ref = reference, relevance = "ölçüm" } },
            possibleCauses = Array.Empty<string>(),
            alternativeExplanations = Array.Empty<string>(),
            missingData = Array.Empty<string>(),
            recommendedChecks = Array.Empty<string>(),
        };

        [GeneratedRegex("<flight_context>\\s*(.*?)\\s*</flight_context>", RegexOptions.Singleline)]
        private static partial Regex ContextPattern();
    }
}
