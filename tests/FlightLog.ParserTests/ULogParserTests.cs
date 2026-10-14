using System.Globalization;
using FlightLog.LogParsing.ULog;

namespace FlightLog.ParserTests;

public sealed class ULogParserTests
{
    public static TheoryData<string> Samples =>
    [
        "sample.ulg",
        "sample_log_small.ulg",
        "sample_appended_multiple.ulg",
        "sample_logging_tagged_and_default_params.ulg",
        "sample_px4_events.ulg",
    ];

    private static (ParserReport Report, AggregatingSink Sink) Parse(string file)
    {
        var sink = new AggregatingSink();
        using var stream = Fixtures.Open(Path.Combine("px4", file));
        var report = new ULogParser().Parse(stream, sink, new ParserLimits(), TestContext.Current.CancellationToken);
        return (report, sink);
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void Every_series_and_value_matches_pyulog(string file)
    {
        var expected = Fixtures.Expected<ExpectedULog>(file);

        var (report, sink) = Parse(file);

        sink.ShouldMatch(expected.Series);
        report.HeaderTimestampUs.Should().Be(expected.HeaderTimestamp);
        report.Corrupt.Should().Be(expected.Corrupt);
        report.Truncated.Should().BeFalse();
        report.SeriesCount.Should().Be(expected.Series.Length);
        Math.Max(report.LastTimestampUs ?? 0, expected.HeaderTimestamp).Should().Be(expected.LastTimestamp);
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void Info_parameters_messages_and_dropouts_match_pyulog(string file)
    {
        var expected = Fixtures.Expected<ExpectedULog>(file);

        var (_, sink) = Parse(file);

        foreach (var (key, value) in expected.Info)
        {
            sink.Info.Should().ContainKey(key);
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                && double.TryParse(sink.Info[key], NumberStyles.Float, CultureInfo.InvariantCulture, out var actual))
            {
                actual.Should().Be(number, key);
            }
            else
            {
                sink.Info[key].Should().Be(value, key);
            }
        }

        var initial = sink.Parameters.Where(p => p.TimestampUs is null && !double.IsNaN(p.Value))
            .GroupBy(p => p.Name).ToDictionary(g => g.Key, g => g.Last().Value);
        initial.Should().BeEquivalentTo(expected.InitialParameters);

        var changed = sink.Parameters.Where(p => p.TimestampUs is not null).Select(p => (p.TimestampUs!.Value, p.Name, p.Value)).ToList();
        changed.Should().Equal(expected.ChangedParameters.Select(c => (c[0].GetInt64(), c[1].GetString()!, c[2].GetDouble())));

        var logged = sink.Texts.Where(t => t.Tag is null).Select(t => (t.TimestampUs, (int?)t.Severity, t.Message)).ToList();
        logged.Should().Equal(expected.LoggedMessages.Select(m => (m[0].GetInt64(), (int?)m[1].GetInt32(), m[2].GetString()!)));
        sink.Texts.Count(t => t.Tag is not null).Should().Be(expected.TaggedLoggedMessages);

        sink.Dropouts.Should().Equal(expected.Dropouts.Select(d => (d[0], (int)d[1])));
    }

    [Fact]
    public void Default_parameter_values_are_reported()
    {
        var (_, sink) = Parse("sample_logging_tagged_and_default_params.ulg");

        sink.Parameters.Should().Contain(p => p.DefaultValue != null && double.IsNaN(p.Value));
    }
}
