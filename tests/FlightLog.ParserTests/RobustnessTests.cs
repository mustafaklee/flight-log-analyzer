using FlightLog.LogParsing.DataFlash;
using FlightLog.LogParsing.ULog;

namespace FlightLog.ParserTests;

/// <summary>Untrusted input: damaged files must never crash the parser or run without bounds (NFR-2).</summary>
public sealed class RobustnessTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Files => ["px4/sample_log_small.ulg", "px4/sample_appended_multiple.ulg", "ardupilot/sitl-copter-4.6.3.bin"];

    private static IFlightLogParser ParserFor(string file) =>
        file.EndsWith(".ulg", StringComparison.Ordinal) ? new ULogParser() : new DataFlashParser();

    private static ParserReport Parse(IFlightLogParser parser, byte[] bytes, ParserLimits? limits = null, ILogRecordSink? sink = null) =>
        parser.Parse(new MemoryStream(bytes, writable: false), sink ?? new AggregatingSink(), limits ?? new ParserLimits(), Ct);

    [Theory]
    [MemberData(nameof(Files))]
    public void Truncated_files_are_read_up_to_the_cut(string file)
    {
        var bytes = File.ReadAllBytes(Fixtures.Path(file));
        var parser = ParserFor(file);
        var full = Parse(parser, bytes);

        foreach (var fraction in new[] { 0.9, 0.5, 0.2 })
        {
            var cut = bytes.AsSpan(0, (int)(bytes.Length * fraction) + 1).ToArray();
            var report = Parse(parser, cut);

            // The tail of a file can hold appended sections without data records, so a late cut may lose none.
            report.RecordCount.Should().BeLessThanOrEqualTo(full.RecordCount).And.BePositive();
            if (fraction < 0.3)
            {
                report.RecordCount.Should().BeLessThan(full.RecordCount);
            }

            report.Truncated.Should().BeTrue($"the file was cut at {fraction:P0}");
        }
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void Random_damage_is_skipped_without_crashing(string file)
    {
        var original = File.ReadAllBytes(Fixtures.Path(file));
        var parser = ParserFor(file);
        var random = new Random(4242);

        for (var round = 0; round < 20; round++)
        {
            var bytes = (byte[])original.Clone();
            // Keep the header intact so the damage hits the message stream.
            for (var i = 0; i < 50; i++)
            {
                bytes[random.Next(64, bytes.Length)] = (byte)random.Next(256);
            }

            try
            {
                Parse(parser, bytes);
            }
            catch (LogParseException)
            {
                // A controlled rejection is an acceptable outcome for damaged input.
            }
        }
    }

    [Fact]
    public void A_corrupt_section_in_a_ulog_is_skipped_up_to_the_next_sync_marker()
    {
        var bytes = File.ReadAllBytes(Fixtures.Path("px4/sample_px4_events.ulg"));
        var full = Parse(new ULogParser(), bytes);
        var damaged = (byte[])bytes.Clone();
        Array.Fill(damaged, (byte)0, bytes.Length / 2, 4096);

        var report = Parse(new ULogParser(), damaged);

        report.Corrupt.Should().BeTrue();
        report.RecordCount.Should().BeGreaterThan(full.RecordCount / 2, "data after the damaged section is recovered");
    }

    [Fact]
    public void Random_bytes_are_not_a_ulog()
    {
        var bytes = new byte[10_000];
        new Random(1).NextBytes(bytes);

        var act = () => Parse(new ULogParser(), bytes);

        act.Should().Throw<LogParseException>().Which.Code.Should().Be("ulog.invalid_header");
    }

    [Fact]
    public void Random_bytes_yield_no_dataflash_records()
    {
        var bytes = new byte[10_000];
        new Random(1).NextBytes(bytes);

        var report = Parse(new DataFlashParser(), bytes);

        report.RecordCount.Should().Be(0);
        report.SkippedBytes.Should().BeGreaterThan(9_000);
    }

    [Fact]
    public void Empty_input_is_handled()
    {
        var ulog = () => Parse(new ULogParser(), []);
        ulog.Should().Throw<LogParseException>().Which.Code.Should().Be("ulog.header_too_short");

        Parse(new DataFlashParser(), []).RecordCount.Should().Be(0);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void Resource_limits_stop_parsing_with_a_stable_code(string file)
    {
        var bytes = File.ReadAllBytes(Fixtures.Path(file));

        var act = () => Parse(ParserFor(file), bytes, new ParserLimits { MaxSeries = 3 });

        act.Should().Throw<LogParseException>().Which.Code.Should().Be("parser.too_many_series");
    }

    [Fact]
    public void Cancellation_is_honoured()
    {
        var bytes = File.ReadAllBytes(Fixtures.Path("px4/sample.ulg"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => new ULogParser().Parse(new MemoryStream(bytes), new AggregatingSink(), new ParserLimits(), cts.Token);

        act.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void Timeout_stops_parsing()
    {
        var bytes = File.ReadAllBytes(Fixtures.Path("ardupilot/sitl-copter-4.6.3.bin"));

        var act = () => Parse(new DataFlashParser(), bytes, new ParserLimits { Timeout = TimeSpan.Zero });

        act.Should().Throw<LogParseException>().Which.Code.Should().Be("parser.timeout");
    }
}
