using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlightLog.IntegrationTests.Infrastructure;

namespace FlightLog.IntegrationTests;

/// <summary>
/// The web client's types are generated from <c>web/src/api/openapi.json</c> (ADR-0010). This test fails when the API
/// changes without that file being updated. To update it: <c>UPDATE_OPENAPI_SNAPSHOT=1 dotnet test ... --filter-class "*OpenApiContractTests"</c>,
/// then <c>npm run api:types</c> in <c>web/</c>.
/// </summary>
public sealed class OpenApiContractTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    [Fact]
    public async Task Published_contract_matches_the_web_client_snapshot()
    {
        var response = await factory.CreateClient().GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var live = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        live["servers"] = null; // The test server's address is not part of the contract.
        var actual = live.ToJsonString(Indented).ReplaceLineEndings("\n") + "\n";

        var path = Path.Combine(RepositoryRoot(), "web", "src", "api", "openapi.json");
        if (Environment.GetEnvironmentVariable("UPDATE_OPENAPI_SNAPSHOT") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, actual, TestContext.Current.CancellationToken);
        }

        File.Exists(path).Should().BeTrue("the snapshot is committed with the web client");
        var expected = (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).ReplaceLineEndings("\n");
        actual.Should().Be(expected, "the API contract changed: update the snapshot and regenerate the web client types");
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "FlightLog.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
