using System.Text.Json;
using FlightLog.Application.Ai;
using FlightLog.Domain.Analysis;

namespace FlightLog.UnitTests.Ai;

public sealed class FlightReportValidatorTests
{
    private static readonly FlightContext Context = FlightContextBuilder.Build(ContextFixtures.Input(), new(60_000, 60, 80, 40, 60));

    private static readonly string[] Questions = ["Pervaneler değişti mi?"];
    private static readonly string[] Limitations = ["Batarya kuralı uygulanamadı."];

    private static string Answer(object items, string summary = "Uçuşta t=60 s ile t=70 s arasında yüksek titreşim var.") => JsonSerializer.Serialize(new
    {
        summary,
        plainSummary = "Titreşim yüksekti.",
        overallAssessment = "significant_issues",
        items,
        followUpQuestions = Questions,
        limitations = Limitations,
    });

    private static object Item(string statement, params string[] refs) => new
    {
        title = "Yüksek titreşim",
        kind = "issue",
        statement,
        severity = "critical",
        confidence = "medium",
        confidenceReason = "Tek ölçüm eşiği aşıyor.",
        evidence = refs.Select(r => new { @ref = r, relevance = "ölçüm" }),
        possibleCauses = new[] { "Pervane dengesizliği" },
        alternativeExplanations = new[] { "Gevşek montaj" },
        missingData = Array.Empty<string>(),
        recommendedChecks = new[] { "Pervaneleri kontrol edin" },
    };

    [Fact]
    public void Cited_references_are_resolved_to_context_items()
    {
        var report = FlightReportValidator.Validate(Answer(new[] { Item("VibeX en fazla 42.46 m/s/s oldu, eşik 30.", "F2", "S2", "[P1]") }), Context);

        var item = report.Items.Should().ContainSingle().Subject;
        item.Supported.Should().BeTrue();
        item.Evidence.Select(e => e.Ref).Should().Equal("F2", "S2", "P1");
        item.Evidence[0].FindingId.Should().Be(ContextFixtures.VibeId);
        item.Evidence[0].StartUs.Should().Be(ContextFixtures.Start + 60_000_000);
        item.Severity.Should().Be(FindingSeverity.Critical);
        item.Kind.Should().Be(AiItemKind.Issue);
        item.UnverifiedNumbers.Should().BeEmpty();
        report.OverallAssessment.Should().Be(AiOverallAssessment.SignificantIssues);
        report.Validation.Should().BeEquivalentTo(new AiReportValidationDto(1, 1, 0, [], 0, []));
    }

    [Fact]
    public void An_item_without_any_existing_reference_is_marked_unsupported()
    {
        var report = FlightReportValidator.Validate(Answer(new[] { Item("Motor 3 arızalandı.", "F9", "X1"), Item("Titreşim yüksek.", "F2", "F9") }), Context);

        report.Items[0].Supported.Should().BeFalse();
        report.Items[0].Evidence.Should().BeEmpty();
        report.Items[0].UnknownRefs.Should().Equal("F9", "X1");
        report.Items[1].Supported.Should().BeTrue("one existing reference is enough");
        report.Items[1].UnknownRefs.Should().Equal("F9");
        report.Validation.UnsupportedItems.Should().Be(1);
        report.Validation.UnknownRefs.Should().Equal("F9", "X1");
    }

    [Fact]
    public void Numbers_not_in_the_context_are_flagged()
    {
        var report = FlightReportValidator.Validate(
            Answer(new[] { Item("VibeX 55.2 m/s/s'ye çıktı; voltaj 11.9 V civarındaydı ve 3 kez uyarı geldi.", "F2") }, "Akım 25,7 A idi."), Context);

        report.Items[0].UnverifiedNumbers.Should().Equal(["55.2"], "11.9 is a rounding of 11.87 and small counts are not checked");
        report.Validation.SummaryUnverifiedNumbers.Should().Equal("25,7");
        report.Validation.UnverifiedNumbers.Should().Be(1);
    }

    [Theory]
    [InlineData("Yazılım 4.6.3 sürümünde.")]
    [InlineData("EKF3 ve GPS2 kontrol edilmeli; F2 kanıtı.")]
    [InlineData("Kapsam %85 idi, t=20 s'de başladı.")]
    [InlineData("Maksimum 42.5, ortalama 18.25.")]
    [InlineData("-42.46 değeri.")]
    public void Versions_identifiers_and_roundings_are_not_flagged(string text) =>
        FlightReportValidator.UnverifiedNumbers([text], Context.Numbers).Should().BeEmpty();

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"summary\":\"x\"}")]
    [InlineData("{\"summary\":\"x\",\"overallAssessment\":\"fine\",\"items\":[]}")]
    public void An_answer_that_is_not_a_report_is_rejected(string answer)
    {
        var act = () => FlightReportValidator.Validate(answer, Context);

        act.Should().Throw<AiReportFormatException>();
    }

    [Fact]
    public void An_empty_report_is_valid_when_nothing_deserves_attention()
    {
        var answer = JsonSerializer.Serialize(new
        {
            summary = "Belirgin bir sorun yok.",
            plainSummary = "",
            overallAssessment = "no_issues_found",
            items = Array.Empty<object>(),
            followUpQuestions = Array.Empty<string>(),
            limitations = Array.Empty<string>(),
        });

        var report = FlightReportValidator.Validate(answer, Context);

        report.Items.Should().BeEmpty();
        report.OverallAssessment.Should().Be(AiOverallAssessment.NoIssuesFound);
    }

    [Fact]
    public void The_validated_report_round_trips_through_storage_and_renders_as_markdown()
    {
        var report = FlightReportValidator.Validate(Answer(new[] { Item("Titreşim <script>.", "F2"), Item("Uydurma.", "Z1") }), Context);
        var stored = JsonSerializer.Serialize(report, FlightReportValidator.StorageOptions);
        var back = JsonSerializer.Deserialize<AiReportDto>(stored, FlightReportValidator.StorageOptions)!;
        back.Items.Should().HaveCount(2);
        stored.Should().Contain("\"severity\":\"Critical\"");

        var dto = new AiAnalysisDto(
            Guid.NewGuid(), Guid.NewGuid(), ContextFixtures.RunId, AiAnalysisStatus.Succeeded, "Anthropic", "claude-opus-5-5", null, FlightReportPrompt.Version,
            FlightContextBuilder.Version, Context.Hash, Context.Summary, ContextFixtures.Start, Guid.NewGuid(), 1, 0, DateTimeOffset.UnixEpoch, null, null,
            DateTimeOffset.UnixEpoch, 1000, 1, 900, 100, 50, 0, 0, 0.0014m, "end_turn", null, null, "c", back);
        var markdown = FlightReportMarkdown.Render(dto, "Test [uçuşu]");

        markdown.Should().Contain("# AI analiz raporu: Test \\[uçuşu\\]");
        markdown.Should().Contain("**F2** vibration.level title (t=60.0 s–t=70.0 s)");
        markdown.Should().Contain("## Desteklenmeyen iddialar").And.Contain("Bağlamda bulunmayan referanslar (yok sayıldı): Z1");
        markdown.Should().Contain("&lt;script&gt;").And.NotContain("<script>");
    }
}
