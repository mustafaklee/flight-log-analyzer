using System.Net;
using System.Text;
using System.Text.Json;
using FlightLog.Application.Ai;
using FlightLog.Infrastructure.Ai;
using Microsoft.Extensions.Options;

namespace FlightLog.UnitTests.Ai;

/// <summary>The Claude adapter against a stubbed HTTP endpoint: request shape, answer mapping, error classes.</summary>
public sealed class AnthropicProviderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly AiProviderRequest Request = new("system text", "user text", FlightReportPrompt.OutputSchema, 16_000);

    private static AnthropicFlightAnalysisProvider Provider(StubHandler handler, string? key = "test-key") =>
        new(Options.Create(new AiOptions { ApiKey = key, Model = "claude-opus-5-5", Effort = "high", ClientMaxRetries = 0, BaseUrl = "https://api.test" }), new HttpClient(handler));

    private const string Success = """
        {
          "id": "msg_1", "type": "message", "role": "assistant", "model": "claude-opus-5-5",
          "content": [{"type": "text", "text": "{\"summary\":\"ok\"}"}],
          "stop_reason": "end_turn", "stop_sequence": null,
          "usage": {"input_tokens": 1200, "output_tokens": 340, "cache_read_input_tokens": 100, "cache_creation_input_tokens": 0}
        }
        """;

    [Fact]
    public async Task Sends_the_schema_effort_and_messages_and_reads_text_and_usage()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Success);
        using var provider = Provider(handler);

        var response = await provider.CompleteAsync(Request, Ct);

        response.Text.Should().Be("{\"summary\":\"ok\"}");
        response.StopReason.Should().Be("end_turn");
        response.Model.Should().Be("claude-opus-5-5");
        response.Usage.Should().Be(new FlightLog.Domain.Analysis.AiUsage(1200, 340, 100, 0));

        handler.LastUri!.AbsoluteUri.Should().Be("https://api.test/v1/messages");
        handler.LastHeaders!.Should().ContainKey("x-api-key");
        using var body = JsonDocument.Parse(handler.LastBody!);
        var root = body.RootElement;
        root.GetProperty("model").GetString().Should().Be("claude-opus-5-5");
        root.GetProperty("max_tokens").GetInt32().Should().Be(16_000);
        root.GetProperty("system").GetString().Should().Be("system text");
        root.GetProperty("messages")[0].GetProperty("role").GetString().Should().Be("user");
        root.GetProperty("messages")[0].GetProperty("content").GetString().Should().Be("user text");
        var output = root.GetProperty("output_config");
        output.GetProperty("effort").GetString().Should().Be("high");
        output.GetProperty("format").GetProperty("type").GetString().Should().Be("json_schema");
        output.GetProperty("format").GetProperty("schema").GetProperty("required").EnumerateArray().Select(e => e.GetString())
            .Should().Contain(["summary", "items", "limitations"]);
        root.TryGetProperty("temperature", out _).Should().BeFalse("sampling parameters are not sent");
    }

    [Fact]
    public async Task A_refusal_comes_back_with_its_stop_reason()
    {
        var refusal = """
            {"id":"msg_2","type":"message","role":"assistant","model":"claude-opus-5-5","content":[],
             "stop_reason":"refusal","stop_details":{"type":"refusal","category":"cyber","explanation":"declined"},
             "usage":{"input_tokens":10,"output_tokens":0}}
            """;
        using var provider = Provider(new StubHandler(HttpStatusCode.OK, refusal));

        var response = await provider.CompleteAsync(Request, Ct);

        response.StopReason.Should().Be("refusal");
        response.Text.Should().BeNull();
        response.RefusalExplanation.Should().Be("declined");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, AiProviderErrorKind.Authentication)]
    [InlineData(HttpStatusCode.Forbidden, AiProviderErrorKind.Authentication)]
    [InlineData(HttpStatusCode.TooManyRequests, AiProviderErrorKind.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, AiProviderErrorKind.Transient)]
    [InlineData((HttpStatusCode)529, AiProviderErrorKind.Transient)]
    [InlineData(HttpStatusCode.BadRequest, AiProviderErrorKind.Rejected)]
    [InlineData(HttpStatusCode.NotFound, AiProviderErrorKind.Rejected)]
    public async Task Http_errors_map_to_retry_classes(HttpStatusCode status, AiProviderErrorKind expected)
    {
        var error = """{"type":"error","error":{"type":"invalid_request_error","message":"model: not found"}}""";
        using var provider = Provider(new StubHandler(status, error));

        var act = () => provider.CompleteAsync(Request, Ct);

        var thrown = (await act.Should().ThrowAsync<AiProviderException>()).Which;
        thrown.Kind.Should().Be(expected);
        if (expected == AiProviderErrorKind.Rejected)
        {
            thrown.Message.Should().Be($"HTTP {(int)status}: model: not found");
        }
    }

    [Fact]
    public async Task Without_a_key_the_provider_is_not_configured()
    {
        using var provider = Provider(new StubHandler(HttpStatusCode.OK, Success), key: null);

        provider.IsConfigured.Should().BeFalse();
        await provider.Invoking(p => p.CompleteAsync(Request, Ct)).Should().ThrowAsync<AiProviderException>();
    }

    [Theory]
    [InlineData("low", "low")]
    [InlineData("MAX", "max")]
    [InlineData("xhigh", "xhigh")]
    [InlineData("nonsense", "high")]
    public void Effort_names_are_parsed(string value, string expected) =>
        AnthropicFlightAnalysisProvider.ParseEffort(value).ToString().ToLowerInvariant().Should().Be(expected);

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public Uri? LastUri { get; private set; }

        public string? LastBody { get; private set; }

        public Dictionary<string, string>? LastHeaders { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            LastHeaders = request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(",", h.Value));
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
