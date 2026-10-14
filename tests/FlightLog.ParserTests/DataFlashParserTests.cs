using FlightLog.LogParsing.DataFlash;

namespace FlightLog.ParserTests;

public sealed class DataFlashParserTests
{
    private const string Sitl = "sitl-copter-4.6.3.bin";

    private static (ParserReport Report, AggregatingSink Sink) Parse(string file, Func<LogSeries, bool>? keep = null)
    {
        var sink = new AggregatingSink { KeepRecords = keep ?? (_ => false) };
        using var stream = Fixtures.Open(Path.Combine("ardupilot", file));
        var report = new DataFlashParser().Parse(stream, sink, new ParserLimits(), TestContext.Current.CancellationToken);
        return (report, sink);
    }

    [Fact]
    public void Every_series_and_value_matches_pymavlink()
    {
        var expected = Fixtures.Expected<ExpectedDataFlash>(Sitl);

        var (report, sink) = Parse(Sitl);

        sink.ShouldMatch(expected.Series);
        report.Corrupt.Should().BeFalse();
        report.SkippedBytes.Should().Be(0);

        // SITL was stopped while writing: the last message is cut off, as in many real logs.
        report.Truncated.Should().BeTrue();
    }

    [Fact]
    public void Text_messages_parameters_modes_and_version_match_pymavlink()
    {
        var expected = Fixtures.Expected<ExpectedDataFlash>(Sitl);

        var (_, sink) = Parse(Sitl, s => s.Message is "MSG" or "PARM" or "MODE" or "VER");

        sink.Records.Where(r => r.Series.Message == "MSG").Select(r => (r.TimestampUs, r.Text("Message")))
            .Should().Equal(expected.Messages.Select(m => (m[0].GetInt64(), m[1].GetString())));
        sink.Records.Where(r => r.Series.Message == "PARM").GroupBy(r => r.Text("Name")!).ToDictionary(g => g.Key, g => g.First().Value("Value"))
            .Should().BeEquivalentTo(expected.Parameters);
        sink.Records.Where(r => r.Series.Message == "MODE").Select(r => (r.TimestampUs, (long)r.Value("Mode")))
            .Should().Equal(expected.Modes.Select(m => (m[0], m[1])));
        sink.Records.First(r => r.Series.Message == "VER").Text("FWS").Should().Be(expected.Firmware);
    }

    [Fact]
    public void Units_instances_and_pending_multipliers_come_from_the_log()
    {
        var (_, sink) = Parse(Sitl);

        var gps = sink.Series[("GPS", 0)].Series;
        gps.Fields[gps.IndexOf("Alt")].Unit.Should().Be("m");
        gps.Fields[gps.IndexOf("Alt")].Multiplier.Should().BeNull("the 'e' format already scales centimetres to metres");
        gps.Fields[gps.IndexOf("Lat")].Multiplier.Should().BeNull("the 'L' format already scales to degrees");
        gps.Fields[gps.IndexOf("GMS")].Multiplier.Should().Be(0.001, "GMS is logged in milliseconds of unit 's'");

        var mag = sink.Series[("MAG", 0)].Series;
        mag.Fields[mag.IndexOf("MagX")].Multiplier.Should().Be(0.001);

        sink.Series.Keys.Should().Contain([("IMU", 0), ("IMU", 1)], "the IMU field marked '#' separates instances");
        sink.Series[("ATT", 0)].Series.Fields.Single(f => f.Name == "Roll").Unit.Should().Be("deg");
    }
}
