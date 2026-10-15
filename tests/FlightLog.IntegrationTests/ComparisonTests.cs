using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FlightLog.IntegrationTests.Infrastructure;

namespace FlightLog.IntegrationTests;

/// <summary>Comparing flights of different autopilots, and the CSV/JSON exports with their audit entries.</summary>
public sealed class ComparisonTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] SitlLog => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "ardupilot", "sitl-copter-4.6.3.bin"));

    [Fact]
    public async Task Flights_of_different_autopilots_are_compared_with_notes()
    {
        var (client, px4, sitl, _) = await TwoFlightsAsync();

        var comparison = await (await client.GetAsync($"/api/v1/flights/compare?ids={px4}&ids={sitl}", Ct)).ReadAsync<Comparison>(HttpStatusCode.OK);
        comparison.Version.Should().Be("comparison-v1");
        comparison.Flights.Select(f => f.Id).Should().Equal(px4, sitl);
        comparison.Flights.Select(f => f.Autopilot).Should().Equal("Px4", "ArduPilot");
        comparison.Flights.Should().OnlyContain(f => f.AnalysisRunId != null && f.DurationSeconds > 0);
        comparison.Notes.Select(n => n.Code).Should().Contain("different_autopilot");
        comparison.Limitations.Should().NotBeEmpty();

        var roll = comparison.Signals.Single(s => s.Name == "attitude.roll");
        roll.Values.Should().HaveCount(2).And.OnlyContain(v => v != null && v.Samples > 0);
        roll.Values.Should().OnlyContain(v => v!.Min <= v.P5 && v.P5 <= v.P50 && v.P50 <= v.P95 && v.P95 <= v.Max);
        comparison.Rules.Should().NotBeEmpty().And.OnlyContain(r => r.Values.Length == 2);

        // Vibration metrics of ArduPilot and PX4 are different quantities; each says which autopilot it is comparable within.
        comparison.Flights.Where(f => f.Vibration != null).Should().OnlyContain(f => f.Vibration!.ComparableWithin == f.Autopilot);
        if (comparison.Flights.Any(f => f.Vibration == null))
        {
            comparison.Notes.Select(n => n.Code).Should().Contain("no_vibration_metric");
        }
    }

    [Fact]
    public async Task Comparison_requires_two_to_six_readable_flights()
    {
        var (client, px4, sitl, _) = await TwoFlightsAsync();
        (await client.GetAsync($"/api/v1/flights/compare?ids={px4}", Ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var seven = string.Join("&", Enumerable.Range(0, 7).Select(_ => $"ids={Guid.NewGuid()}"));
        (await client.GetAsync($"/api/v1/flights/compare?{seven}", Ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.GetAsync($"/api/v1/flights/compare?ids={px4}&ids={px4}", Ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // A flight of another team is reported as missing, and nothing of the readable flight leaks.
        var outsider = await factory.CreateUserClientAsync();
        (await outsider.GetAsync($"/api/v1/flights/compare?ids={px4}&ids={sitl}", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var (other, otherPx4, _, _) = await TwoFlightsAsync();
        (await client.GetAsync($"/api/v1/flights/compare?ids={px4}&ids={otherPx4}", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await other.GetAsync($"/api/v1/flights/{px4}/export/findings.csv", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Exports_are_files_with_their_sources_and_are_audited()
    {
        var (client, px4, sitl, teamId) = await TwoFlightsAsync();
        await client.PostAsJsonAsync($"/api/v1/flights/{sitl}/comments", new { body = "=HYPERLINK(\"http://evil\")" }, Ct);

        var findings = await client.GetAsync($"/api/v1/flights/{sitl}/export/findings.csv", Ct);
        findings.StatusCode.Should().Be(HttpStatusCode.OK);
        findings.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");
        findings.Content.Headers.ContentDisposition!.FileName.Should().Contain("bulgular").And.EndWith(".csv");
        var bytes = await findings.Content.ReadAsByteArrayAsync(Ct);
        bytes.Take(3).Should().Equal(0xEF, 0xBB, 0xBF);
        var lines = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines[0].Should().Be("# export: findings");
        lines.Should().Contain(l => l.StartsWith("# analysis: ", StringComparison.Ordinal));
        var header = lines.First(l => !l.StartsWith('#'));
        header.Should().StartWith("finding_id,rule_id,rule_version,category,severity").And.EndWith("review_status,assignee,flagged,comments");
        lines.Should().Contain(l => l.Contains(",data.gaps,", StringComparison.Ordinal) && l.EndsWith(",Open,,false,0", StringComparison.Ordinal));

        var events = await client.GetAsync($"/api/v1/flights/{sitl}/export/events.csv", Ct);
        var eventLines = (await events.Content.ReadAsStringAsync(Ct)).TrimStart('﻿').Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        eventLines.First(l => !l.StartsWith('#')).Should().Be("time_s,timestamp_us,utc,kind,severity,source,text");
        eventLines.Length.Should().BeGreaterThan(5);

        var report = await client.GetAsync($"/api/v1/flights/{sitl}/export/report.json", Ct);
        report.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        using var json = JsonDocument.Parse(await report.Content.ReadAsStringAsync(Ct));
        var root = json.RootElement;
        root.GetProperty("format").GetString().Should().Be("flightlog.flight-report.v1");
        root.GetProperty("sources").EnumerateArray().Select(s => s.GetString()).Should().Contain(s => s!.StartsWith("rule analysis ", StringComparison.Ordinal));
        root.GetProperty("analysis").GetProperty("findings").GetArrayLength().Should().BeGreaterThan(0);
        root.GetProperty("comments")[0].GetProperty("body").GetString().Should().StartWith("=HYPERLINK", "JSON keeps text as written");
        root.GetProperty("aiReport").ValueKind.Should().Be(JsonValueKind.Null);
        var text = root.GetRawText();
        text.Should().NotContain("@flightlog.test", "exports never carry e-mail addresses");
        text.Should().NotContain("latitude", "exports never carry positions");

        var compared = await client.GetAsync($"/api/v1/flights/compare/export?ids={px4}&ids={sitl}&format=Csv", Ct);
        compared.Content.Headers.ContentDisposition!.FileName.Should().StartWith("karsilastirma-");
        var compareLines = (await compared.Content.ReadAsStringAsync(Ct)).TrimStart('﻿').Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        compareLines.Should().Contain("# export: flight comparison");
        compareLines.Should().Contain(l => l.StartsWith("# note different_autopilot: ", StringComparison.Ordinal));
        compareLines.First(l => !l.StartsWith('#')).Should().Be("section,metric,unit,sample,sitl");
        compareLines.Should().Contain(l => l.StartsWith("flight,autopilot,,Px4,ArduPilot", StringComparison.Ordinal));
        var comparedJson = await client.GetAsync($"/api/v1/flights/compare/export?ids={px4}&ids={sitl}&format=Json", Ct);
        (await comparedJson.Content.ReadFromJsonAsync<Comparison>(ApiClientExtensions.Json, Ct))!.Flights.Should().HaveCount(2);

        var exported = await (await client.GetAsync($"/api/v1/teams/{teamId}/audit-log?action=data.exported&pageSize=100", Ct)).ReadAsync<Paged<AuditEntry>>(HttpStatusCode.OK);
        exported.Items.Select(a => a.Data["kind"]).Should().Contain(["findings", "events", "flight-report", "comparison"]);
        exported.Items.Where(a => a.Data["kind"] == "comparison").Should().HaveCount(4, "a comparison export is recorded on each flight, twice");
    }

    [Fact]
    public async Task Csv_cells_that_look_like_formulas_are_neutralized()
    {
        var (client, px4, sitl, _) = await TwoFlightsAsync();
        var flight = await (await client.GetAsync($"/api/v1/flights/{sitl}", Ct)).ReadAsync<VersionedFlight>(HttpStatusCode.OK);
        (await client.PutAsJsonAsync($"/api/v1/flights/{sitl}", new { name = "=cmd|'/c calc'!A1", version = flight.Version }, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        var csv = await (await client.GetAsync($"/api/v1/flights/compare/export?ids={px4}&ids={sitl}", Ct)).Content.ReadAsStringAsync(Ct);
        csv.Split("\r\n").First(l => l.StartsWith("section,", StringComparison.Ordinal)).Should().Be("section,metric,unit,sample,'=cmd|'/c calc'!A1");
    }

    /// <summary>A team with an analyzed PX4 flight ("sample") and an analyzed ArduPilot SITL flight ("sitl").</summary>
    private async Task<(HttpClient Client, Guid Px4, Guid Sitl, Guid TeamId)> TwoFlightsAsync()
    {
        var client = await factory.CreateUserClientAsync();
        var (team, project) = await client.CreateTeamAndProjectAsync();
        var px4 = await (await client.UploadLogAsync(project.Id, Fixtures.Px4Sample, "sample.ulg")).ReadAsync<UploadResult>(HttpStatusCode.Created);
        var sitl = await (await client.UploadLogAsync(project.Id, SitlLog, "sitl.bin")).ReadAsync<UploadResult>(HttpStatusCode.Created);
        await factory.ProcessQueueAsync();
        return (client, px4.Flight.Id, sitl.Flight.Id, team.Id);
    }
}

internal sealed record Comparison(
    string Version, ComparedFlight[] Flights, ComparedSignal[] Signals, ComparedRule[] Rules, ComparabilityNote[] Notes, string[] Limitations);

internal sealed record ComparedFlight(Guid Id, string Name, string Autopilot, double? DurationSeconds, Guid? AnalysisRunId, Vibration? Vibration);

internal sealed record Vibration(string Metric, string ComparableWithin, double Mean, double P95, double Max);

internal sealed record ComparedSignal(string Name, string? Unit, bool Comparable, string? Note, SignalStats?[] Values);

internal sealed record SignalStats(long Samples, double RateHz, double Min, double Max, double Mean, double P5, double P50, double P95);

internal sealed record ComparedRule(string RuleId, object?[] Values);

internal sealed record ComparabilityNote(string Code, string Message, Guid[] FlightIds);

internal sealed record VersionedFlight(Guid Id, string Name, uint Version);
