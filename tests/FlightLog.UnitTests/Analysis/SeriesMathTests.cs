using FlightLog.Application.Analysis;

namespace FlightLog.UnitTests.Analysis;

public sealed class SeriesMathTests
{
    private static readonly TimeSpan Sec = TimeSpan.FromSeconds(1);

    /// <summary>One sample per 100 ms, starting at t = 0.</summary>
    private static TimeSeries Series(params double[] values) =>
        new("s", null, [.. Enumerable.Range(0, values.Length).Select(i => i * 100_000L)], values);

    private static readonly TimeRange[] Everything = [new(0, long.MaxValue)];

    [Fact]
    public void Intervals_end_at_the_first_sample_where_the_condition_no_longer_holds()
    {
        var s = Series(0, 5, 6, 7, 0, 0);

        var result = SeriesMath.FindIntervals(s, v => v > 1, Everything, TimeSpan.Zero, TimeSpan.Zero, Sec);

        result.Should().ContainSingle().Which.Should().Be(new ConditionInterval(100_000, 400_000, 3, 5, 7));
    }

    [Fact]
    public void Short_intervals_are_dropped_and_close_ones_merged()
    {
        // Two violations 200 ms apart, each 100 ms long, and a single-sample blip later.
        var s = Series(9, 0, 0, 9, 0, 0, 0, 0, 0, 0, 9, 0);

        var merged = SeriesMath.FindIntervals(s, v => v > 1, Everything, TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(250), Sec);

        merged.Should().ContainSingle().Which.Range.Should().Be(new TimeRange(0, 400_000));
    }

    [Fact]
    public void Missing_data_ends_an_interval_and_invalid_samples_are_skipped()
    {
        var s = new TimeSeries("s", null, [0, 100_000, 5_000_000, 5_100_000], [9, 9, 9, double.NaN]);

        var result = SeriesMath.FindIntervals(s, v => v > 1, Everything, TimeSpan.Zero, TimeSpan.Zero, Sec);

        result.Select(r => r.Range).Should().Equal(new TimeRange(0, 100_000), new TimeRange(5_000_000, 5_000_000));
    }

    [Fact]
    public void Only_samples_inside_the_windows_count()
    {
        var s = Series(9, 9, 9, 9, 9, 9);

        var result = SeriesMath.FindIntervals(s, v => v > 1, [new TimeRange(200_000, 300_000)], TimeSpan.Zero, TimeSpan.Zero, Sec);

        result.Should().ContainSingle().Which.Range.Should().Be(new TimeRange(200_000, 300_000));
    }

    [Fact]
    public void Coverage_is_the_share_of_window_time_with_valid_data()
    {
        var s = new TimeSeries("s", null, [0, 100_000, 200_000, 300_000, 400_000], [1, 1, double.NaN, 1, 1]);

        SeriesMath.Coverage(s, [new TimeRange(0, 400_000)], Sec).Should().BeApproximately(0.75, 1e-9);
        SeriesMath.Coverage(s, [new TimeRange(0, 800_000)], Sec).Should().BeApproximately(0.375, 1e-9);
        SeriesMath.Coverage(s, [new TimeRange(0, 400_000)], TimeSpan.FromMilliseconds(50)).Should().Be(0);
    }

    [Fact]
    public void Gaps_and_median_interval()
    {
        long[] t = [0, 100_000, 200_000, 2_200_000, 2_300_000];

        SeriesMath.MedianIntervalUs(t).Should().Be(100_000);
        SeriesMath.MedianIntervalUs([5]).Should().BeNull();
        SeriesMath.FindGaps(t, [new TimeRange(0, 3_000_000)], 1_000_000).Should().Equal(new TimeRange(200_000, 2_200_000));
        SeriesMath.FindGaps(t, [new TimeRange(1_500_000, 3_000_000)], 1_000_000).Should().BeEmpty("only 0.7 s of the gap lies in the window");
    }
}
