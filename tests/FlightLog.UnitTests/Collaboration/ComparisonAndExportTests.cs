using System.Text;
using FlightLog.Application.Analysis;
using FlightLog.Application.Collaboration;
using FlightLog.Application.Comparison;
using FlightLog.Application.Exports;
using FlightLog.Domain.Analysis;
using FlightLog.Domain.Common;
using FlightLog.Domain.FlightData;
using FlightLog.UnitTests.Ai;

namespace FlightLog.UnitTests.Collaboration;

public sealed class ComparisonAndExportTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly double[] OneToTen = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];

    [Theory]
    [InlineData(0.0, 1.0)]
    [InlineData(0.5, 5.5)]
    [InlineData(0.95, 9.55)]
    [InlineData(1.0, 10.0)]
    public void Percentiles_interpolate_between_ranks(double p, double expected) =>
        FlightComparisonCalculator.Percentile(OneToTen, p).Should().Be(expected);

    [Fact]
    public void Statistics_use_only_finite_samples_inside_the_windows()
    {
        var series = new TimeSeries("battery.voltage", "V", [0, 1_000_000, 2_000_000, 3_000_000, 4_000_000], [9, 12, double.NaN, 11, 99]);
        var stats = FlightComparisonCalculator.Statistics(series, [new TimeRange(1_000_000, 3_000_000)])!;
        stats.Samples.Should().Be(2);
        (stats.Min, stats.Max, stats.Mean).Should().Be((11.0, 12.0, 11.5));
        stats.RateHz.Should().Be(1);
        FlightComparisonCalculator.Statistics(series, [new TimeRange(5_000_000, 6_000_000)]).Should().BeNull();
    }

    [Fact]
    public void Mode_time_starts_unknown_and_follows_the_changes_inside_the_windows()
    {
        var start = ContextFixtures.Start;
        IReadOnlyList<FlightEvent> events =
        [
            ContextFixtures.Event(10, FlightEventKind.ModeChange, "STABILIZE"),
            ContextFixtures.Event(40, FlightEventKind.ModeChange, "LOITER"),
            ContextFixtures.Event(45, FlightEventKind.Error, "not a mode"),
        ];
        var modes = FlightComparisonCalculator.Modes(events, [new TimeRange(start, start + 60_000_000)]);
        modes.Select(m => (m.Mode, m.Seconds)).Should().Equal(("STABILIZE", 30.0), ("LOITER", 20.0), ("unknown", 10.0));
        modes.Sum(m => m.Share).Should().BeApproximately(1, 1e-9);
        FlightComparisonCalculator.Modes([], [new TimeRange(start, start + 1)]).Should().BeEmpty();
    }

    [Fact]
    public void Differences_that_limit_the_comparison_are_noted()
    {
        var aircraft = Guid.NewGuid();
        var window = new TimeRange(0, 10_000_000);
        var vibe = new TimeSeries("VIBE.VibeX", "m/s/s", [1_000_000, 2_000_000], [10, 20]);
        var px4 = Input(Autopilot.Px4, "1.15.0", aircraft, window, signals: new() { ["battery.voltage"] = new TimeSeries("battery.voltage", "V", [0, 5_000_000, 9_000_000], [16.8, 16.0, 15.2]) });
        var ardu = Input(Autopilot.ArduPilot, "4.6.3", null, window, vibration: [vibe],
            signals: new() { ["battery.voltage"] = new TimeSeries("battery.voltage", "mV", [0, 1_000_000], [12_600, 12_500]) });
        var ground = Input(Autopilot.ArduPilot, null, aircraft, window, armingKnown: false);
        var unprocessed = Input(Autopilot.ArduPilot, null, aircraft, window) with { Processed = false };

        var result = FlightComparisonCalculator.Compute([px4, ardu, ground, unprocessed], Now);

        result.Version.Should().Be("comparison-v1");
        result.Notes.Select(n => n.Code).Should().Equal(
            "different_autopilot", "different_firmware", "different_or_unknown_aircraft", "not_processed", "no_arming_information", "no_rule_analysis", "no_vibration_metric");
        result.Notes.Single(n => n.Code == "different_or_unknown_aircraft").FlightIds.Should().Equal(ardu.Id);
        result.Notes.Single(n => n.Code == "not_processed").FlightIds.Should().Equal(unprocessed.Id);
        result.Notes.Single(n => n.Code == "no_vibration_metric").FlightIds.Should().Equal(px4.Id, ground.Id);

        var voltage = result.Signals.Single(s => s.Name == "battery.voltage");
        voltage.Comparable.Should().BeFalse();
        voltage.Note.Should().Contain("Units differ").And.Contain("No valid samples in 2 of 4 flights");
        result.Signals.Should().NotContain(s => s.Name == "attitude.roll", "no flight has it");

        result.Flights[0].Battery!.StartVoltage.Should().Be(16.8);
        result.Flights[0].Battery!.MinVoltage.Should().Be(15.2);
        result.Flights[1].Vibration!.ComparableWithin.Should().Be(Autopilot.ArduPilot);
        result.Flights[1].Vibration!.Max.Should().Be(20);
        result.Flights[3].AnalyzedSeconds.Should().BeNull();
        result.Flights[3].Modes.Should().BeEmpty();
    }

    [Fact]
    public void Csv_quotes_neutralizes_formulas_and_writes_invariant_numbers()
    {
        new Csv().Meta("flight", "two\nlines").Row("a,b", "say \"hi\"", 1.5, true, null, "=SUM(A1)", "-1", "@x", -2.25, "ok").ToString()
            .Should().Be("# flight: two lines\r\n\"a,b\",\"say \"\"hi\"\"\",1.5,true,,'=SUM(A1),'-1,'@x,-2.25,ok\r\n");
        Csv.Cell(new DateTimeOffset(2026, 10, 15, 14, 30, 0, TimeSpan.FromHours(3))).Should().Be("2026-10-15T11:30:00.000Z");
        Csv.Cell(double.NaN).Should().BeEmpty();
        Csv.Cell("\tcmd").Should().Be("'\tcmd");
        var bytes = new Csv().Row("ğüşİ").ToBytes();
        bytes.Take(3).Should().Equal(Encoding.UTF8.GetPreamble());
        Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3).Should().Be("ğüşİ\r\n");
    }

    [Fact]
    public void Request_validators_reject_ambiguous_or_empty_requests()
    {
        var compare = new CompareFlightsRequestValidator();
        var a = Guid.NewGuid();
        compare.Validate(new CompareFlightsRequest([a, Guid.NewGuid()])).IsValid.Should().BeTrue();
        compare.Validate(new CompareFlightsRequest([a, a])).IsValid.Should().BeFalse("the same flight twice is one flight");
        compare.Validate(new CompareFlightsRequest([.. Enumerable.Range(0, 7).Select(_ => Guid.NewGuid())])).IsValid.Should().BeFalse();
        compare.Validate(new CompareFlightsRequest([a, Guid.Empty])).IsValid.Should().BeFalse();

        var review = new UpdateFindingReviewRequestValidator();
        review.Validate(new UpdateFindingReviewRequest(null, null, false, null, null)).IsValid.Should().BeFalse("nothing to change");
        review.Validate(new UpdateFindingReviewRequest(null, Guid.NewGuid(), true, null, null)).IsValid.Should().BeFalse("assign and unassign at once");
        review.Validate(new UpdateFindingReviewRequest(null, null, true, null, null)).IsValid.Should().BeTrue();
        review.Validate(new UpdateFindingReviewRequest(null, null, false, null, "note")).IsValid.Should().BeTrue();

        var audit = new AuditQueryValidator();
        audit.Validate(new AuditQuery { PageSize = AuditQuery.MaxPageSize + 1 }).IsValid.Should().BeFalse();
        audit.Validate(new AuditQuery { From = Now, To = Now.AddDays(-1) }).IsValid.Should().BeFalse();
        audit.Validate(new AuditQuery()).IsValid.Should().BeTrue();
    }

    private static ComparisonInput Input(
        Autopilot autopilot, string? firmware, Guid? aircraftId, TimeRange window, bool armingKnown = true,
        Dictionary<string, TimeSeries>? signals = null, IReadOnlyList<TimeSeries>? vibration = null) => new(
        Guid.NewGuid(), autopilot.ToString(), "Campaign", aircraftId, null, autopilot, firmware, "Copter", null, null, 10, true, [window], armingKnown,
        [], signals ?? [], vibration ?? [], null, [], [], null);
}
