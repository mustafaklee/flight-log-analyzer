using FlightLog.LogParsing.DataFlash;
using FlightLog.LogParsing.Normalization;
using FlightLog.LogParsing.Signals;
using FlightLog.LogParsing.ULog;

namespace FlightLog.ParserTests;

public sealed class SignalStorageTests
{
    [Fact]
    public void Codec_round_trips_timestamps_and_values_bit_for_bit()
    {
        long[] timestamps = [5, 1_000_000_000_000, 7, -3, long.MaxValue / 4];
        double[][] columns =
        [
            [0.1, double.NaN, double.PositiveInfinity, -0.0, double.MinValue],
            [1, 2, 3, 4, 5],
        ];

        var data = SignalChunkCodec.Encode(timestamps, columns);
        var decoded = SignalChunkCodec.Decode(data);

        decoded.Timestamps.Should().Equal(timestamps);
        decoded.Columns.Should().HaveCount(2);
        decoded.Columns[0].Select(BitConverter.DoubleToInt64Bits).Should().Equal(columns[0].Select(BitConverter.DoubleToInt64Bits));
        SignalChunkCodec.Decode(data, [1]).Columns.Should().ContainSingle().Which.Should().Equal(columns[1]);
        SignalChunkCodec.Decode(SignalChunkCodec.Encode([], [Array.Empty<double>()])).Timestamps.Should().BeEmpty();
    }

    [Fact]
    public void Codec_rejects_damaged_chunks_with_invalid_data()
    {
        var data = SignalChunkCodec.Encode([1, 2, 3], [[1.0, 2.0, 3.0]]);

        foreach (var (damaged, i) in new[] { data[..10], data[..^4], [.. "XXXX"u8, .. data[4..]], Garbage(data) }.Select((d, i) => (d, i)))
        {
            var act = () => SignalChunkCodec.Decode(damaged);
            act.Should().Throw<InvalidDataException>($"case {i}");
        }

        static byte[] Garbage(byte[] data)
        {
            var copy = data.ToArray();
            copy[8] = 0xFF;
            copy[11] = 0x7F;
            return copy;
        }
    }

    [Theory]
    [InlineData("px4/sample_log_small.ulg")]
    [InlineData("px4/sample_appended_multiple.ulg")]
    [InlineData("ardupilot/sitl-copter-4.6.3.bin")]
    public void Stored_chunks_reproduce_every_value_the_oracle_sees(string file)
    {
        var chunks = new List<SignalChunk>();
        var sink = new SignalChunkSink(chunks.Add);
        using (var stream = Fixtures.Open(file))
        {
            Parser(file).Parse(stream, sink, new ParserLimits(), TestContext.Current.CancellationToken);
        }

        sink.Complete();

        // Replay the decoded chunks into the same aggregation the parser tests use.
        var replay = new AggregatingSink();
        var bySeries = sink.Series.ToDictionary(s => s.Series.Id);
        foreach (var stats in sink.Series)
        {
            replay.OnSeries(stats.Series);
        }

        foreach (var chunk in chunks)
        {
            var stats = bySeries[chunk.SeriesId];
            var decoded = SignalChunkCodec.Decode(chunk.Data);
            decoded.Timestamps.Should().HaveCount(chunk.RecordCount);
            chunk.StartTimestampUs.Should().Be(decoded.Timestamps.Min());
            var values = new double[stats.Series.Fields.Count];
            for (var r = 0; r < chunk.RecordCount; r++)
            {
                Array.Fill(values, double.NaN);
                foreach (var field in stats.Fields)
                {
                    values[stats.Series.IndexOf(field.Field.Name)] = decoded.Columns[field.Column][r];
                }

                replay.OnRecord(new LogRecord(stats.Series, decoded.Timestamps[r], values, []));
            }
        }

        var name = Path.GetFileName(file);
        var expected = file.EndsWith(".bin", StringComparison.Ordinal)
            ? Fixtures.Expected<ExpectedDataFlash>(name).Series
            : Fixtures.Expected<ExpectedULog>(name).Series;

        // Series without numeric fields (text only) are not stored as signals.
        replay.ShouldMatch(expected, s => s.Fields.Count > 0);
        sink.Series.Sum(s => s.ChunkCount).Should().Be(chunks.Count);
        chunks.GroupBy(c => c.SeriesId).Should().OnlyContain(g => g.Select(c => c.Index).SequenceEqual(Enumerable.Range(0, g.Count())));
    }

    [Fact]
    public void Quaternion_conversion_matches_known_rotations()
    {
        var half = Math.Sqrt(0.5);
        SignalNormalizer.QuaternionToEulerDegrees(1, 0, 0, 0).Should().Be((0.0, 0.0, 0.0));
        SignalNormalizer.QuaternionToEulerDegrees(half, 0, 0, half).Yaw.Should().BeApproximately(90, 1e-9);
        SignalNormalizer.QuaternionToEulerDegrees(half, half, 0, 0).Roll.Should().BeApproximately(90, 1e-9);
        SignalNormalizer.QuaternionToEulerDegrees(Math.Cos(Math.PI / 12), 0, Math.Sin(Math.PI / 12), 0).Pitch.Should().BeApproximately(30, 1e-9);
        SignalNormalizer.WrapDegrees(359).Should().BeApproximately(-1, 1e-12);
        SignalNormalizer.WrapDegrees(180).Should().Be(-180);
        SignalNormalizer.WrapDegrees(-180).Should().Be(-180);
        double.IsNaN(SignalNormalizer.WrapDegrees(double.NaN)).Should().BeTrue();
    }

    [Fact]
    public void ArduPilot_normalized_signals_follow_the_source_fields()
    {
        var sink = Normalize("ardupilot/sitl-copter-4.6.3.bin", SignalNormalizer.ForArduPilot, out var normalizer);

        var att = sink.Series[("ATT", 0)];
        var attitude = sink.Series[("norm.attitude", 0)];
        attitude.Records.Should().Be(att.Records);
        attitude.Column("roll").Min.Should().Be(att.Column("Roll").Min);
        attitude.Column("pitch").Max.Should().Be(att.Column("Pitch").Max);
        attitude.Column("yaw").Min.Should().BeGreaterThanOrEqualTo(-180);
        attitude.Column("yaw").Max.Should().BeLessThan(180);

        var gps = sink.Series[("norm.gps", 0)];
        gps.Records.Should().Be(sink.Series[("GPS", 0)].Records, "only GPS instance 0 is used");
        gps.Column("lat").Max.Should().Be(sink.Series[("GPS", 0)].Column("Lat").Max);
        gps.Column("fix_type").Max.Should().Be(6, "SITL reports an RTK fixed solution");

        sink.Series[("norm.battery", 0)].Column("voltage").Max.Should().Be(sink.Series[("BAT", 0)].Column("Volt").Max);

        var roll = attitude.Series.Fields[attitude.Series.IndexOf("roll")];
        roll.Unit.Should().Be("deg");
        normalizer.SourceOf(roll).Should().Be(new NormalizedFieldSource("ATT", 0, "Roll", "none"));
    }

    [Fact]
    public void Px4_normalized_signals_convert_quaternions_and_integer_coordinates()
    {
        var sink = Normalize("px4/sample_log_small.ulg", SignalNormalizer.ForPx4, out var normalizer);

        var attitude = sink.Series[("norm.attitude", 0)];
        attitude.Records.Should().Be(sink.Series[("vehicle_attitude", 0)].Records);
        attitude.Column("roll").Max.Should().BeLessThan(90).And.BeGreaterThan(-90);
        attitude.Column("yaw").Max.Should().BeLessThan(180);

        var gps = sink.Series[("norm.gps", 0)];
        var raw = sink.Series[("vehicle_gps_position", 0)];
        gps.Column("lat").Max.Should().BeApproximately(raw.Column("lat").Max * 1e-7, 1e-12);
        gps.Column("alt_msl").Min.Should().BeApproximately(raw.Column("alt").Min * 1e-3, 1e-9);
        normalizer.SourceOf(gps.Series.Fields[gps.Series.IndexOf("lat")])!.Conversion.Should().Be("1e-7 deg to deg");

        var battery = sink.Series[("norm.battery", 0)];
        var remaining = sink.Series[("battery_status", 0)].Column("remaining");
        battery.Column("remaining").Max.Should().BeApproximately(remaining.Max * 100, 1e-9);
    }

    [Fact]
    public void Px4_modern_gps_fields_are_used_as_is_and_missing_sources_produce_no_signal()
    {
        var modern = Normalize("px4/sample_px4_events.ulg", SignalNormalizer.ForPx4, out _);
        modern.Series[("norm.gps", 0)].Column("lat").Max.Should().Be(modern.Series[("vehicle_gps_position", 0)].Column("latitude_deg").Max);

        var old = Normalize("px4/sample.ulg", SignalNormalizer.ForPx4, out _);
        old.Series.Keys.Should().Contain(("norm.attitude", 0));
        old.Series.Keys.Should().NotContain(("norm.gps", 0), "the log has no GPS topic, so there is no GPS signal (not zeros)");
        old.Series.Keys.Should().NotContain(("norm.battery", 0));
    }

    private static IFlightLogParser Parser(string file) => file.EndsWith(".bin", StringComparison.Ordinal) ? new DataFlashParser() : new ULogParser();

    private static AggregatingSink Normalize(string file, Func<ILogRecordSink, SignalNormalizer> create, out SignalNormalizer normalizer)
    {
        var sink = new AggregatingSink();
        normalizer = create(sink);
        using var stream = Fixtures.Open(file);
        Parser(file).Parse(stream, normalizer, new ParserLimits(), TestContext.Current.CancellationToken);
        return sink;
    }
}
