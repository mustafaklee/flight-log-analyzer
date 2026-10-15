using FlightLog.Application.FlightData;

namespace FlightLog.UnitTests.Application;

public sealed class DownsamplerTests
{
    [Fact]
    public void Keeps_each_buckets_extremes_in_time_order()
    {
        long[] t = [0, 1, 2, 3, 4, 5, 6, 7];
        double[] v = [5, 9, 1, 5, 5, -3, 7, 5];

        var (times, values) = Downsampler.MinMax(t, v, 0, 7, 4);

        // Two buckets: [0..3] has max 9 at t=1 and min 1 at t=2; [4..7] has min -3 at t=5 and max 7 at t=6.
        times.Should().Equal(1, 2, 5, 6);
        values.Should().Equal(9, 1, -3, 7);
    }

    [Fact]
    public void A_bucket_with_only_invalid_samples_stays_a_gap()
    {
        long[] t = [0, 1, 10, 11];
        double[] v = [1, 2, double.NaN, double.NaN];

        var (times, values) = Downsampler.MinMax(t, v, 0, 11, 4);

        times.Should().Equal(0, 1, 10);
        values.Should().Equal(1, 2, null);
    }

    [Fact]
    public void Output_never_exceeds_the_requested_points_much()
    {
        var t = Enumerable.Range(0, 100_000).Select(i => (long)i * 1000).ToArray();
        var v = t.Select(x => Math.Sin(x / 1e6)).ToArray();

        var (times, _) = Downsampler.MinMax(t, v, t[0], t[^1], 500);

        times.Count.Should().BeLessThanOrEqualTo(502);
        times.Should().BeInAscendingOrder();
    }
}
