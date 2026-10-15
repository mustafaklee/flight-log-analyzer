namespace FlightLog.Application.Analysis;

/// <summary>A closed range of log time in microseconds.</summary>
public readonly record struct TimeRange(long StartUs, long EndUs)
{
    public long DurationUs => EndUs - StartUs;

    public double DurationSeconds => DurationUs / 1e6;

    public bool Contains(long t) => t >= StartUs && t <= EndUs;

    public bool Overlaps(TimeRange other) => StartUs <= other.EndUs && other.StartUs <= EndUs;
}

/// <summary>One signal at full resolution, sorted by time, with its multiplier applied. NaN marks invalid samples.</summary>
public sealed class TimeSeries
{
    public TimeSeries(string name, string? unit, long[] timestamps, double[] values)
    {
        ArgumentNullException.ThrowIfNull(timestamps);
        ArgumentNullException.ThrowIfNull(values);
        if (timestamps.Length != values.Length)
        {
            throw new ArgumentException("Timestamps and values must have the same length.", nameof(values));
        }

        Name = name;
        Unit = unit;
        Timestamps = timestamps;
        Values = values;
    }

    public string Name { get; }

    public string? Unit { get; }

    public long[] Timestamps { get; }

    public double[] Values { get; }

    public int Count => Timestamps.Length;
}

/// <summary>A stretch of time in which a condition held, with the values seen in it.</summary>
public sealed record ConditionInterval(long StartUs, long EndUs, int SampleCount, double Min, double Max)
{
    public double DurationSeconds => (EndUs - StartUs) / 1e6;

    public TimeRange Range => new(StartUs, EndUs);
}

/// <summary>Interval arithmetic used by the rules. Pure functions, unit tested on their own.</summary>
public static class SeriesMath
{
    /// <summary>
    /// Finds where <paramref name="condition"/> holds on consecutive valid samples inside <paramref name="windows"/>.
    /// An interval ends at the first sample where the condition no longer holds (or at its last sample when the
    /// data ends); a pause between samples longer than <paramref name="maxSampleGap"/> ends it too, because missing
    /// data is not evidence. Intervals closer than <paramref name="mergeGap"/> are merged, then those shorter than
    /// <paramref name="minDuration"/> are dropped.
    /// </summary>
    public static List<ConditionInterval> FindIntervals(
        TimeSeries series, Func<double, bool> condition, IReadOnlyList<TimeRange> windows, TimeSpan minDuration, TimeSpan mergeGap, TimeSpan maxSampleGap)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(windows);
        var maxGapUs = (long)(maxSampleGap.TotalMilliseconds * 1000);
        var raw = new List<ConditionInterval>();
        long start = 0, last = 0;
        int count = 0;
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        var open = false;

        void Close(long end)
        {
            raw.Add(new ConditionInterval(start, end, count, min, max));
            open = false;
        }

        for (var i = 0; i < series.Count; i++)
        {
            var t = series.Timestamps[i];
            var v = series.Values[i];
            if (!InAny(windows, t))
            {
                if (open)
                {
                    Close(last);
                }

                continue;
            }

            if (open && t - last > maxGapUs)
            {
                Close(last);
            }

            if (!double.IsFinite(v))
            {
                continue;
            }

            if (condition(v))
            {
                if (!open)
                {
                    open = true;
                    start = t;
                    count = 0;
                    min = double.PositiveInfinity;
                    max = double.NegativeInfinity;
                }

                count++;
                min = Math.Min(min, v);
                max = Math.Max(max, v);
                last = t;
            }
            else if (open)
            {
                Close(t);
            }
        }

        if (open)
        {
            Close(last);
        }

        return Merge(raw, mergeGap).Where(r => r.EndUs - r.StartUs >= (long)(minDuration.TotalMilliseconds * 1000)).ToList();
    }

    /// <summary>Merges intervals whose distance is below <paramref name="gap"/>; input in any order.</summary>
    public static List<ConditionInterval> Merge(IEnumerable<ConditionInterval> intervals, TimeSpan gap)
    {
        var gapUs = (long)(gap.TotalMilliseconds * 1000);
        var result = new List<ConditionInterval>();
        foreach (var r in intervals.OrderBy(r => r.StartUs))
        {
            if (result.Count > 0 && r.StartUs - result[^1].EndUs <= gapUs)
            {
                var p = result[^1];
                result[^1] = new ConditionInterval(p.StartUs, Math.Max(p.EndUs, r.EndUs), p.SampleCount + r.SampleCount, Math.Min(p.Min, r.Min), Math.Max(p.Max, r.Max));
            }
            else
            {
                result.Add(r);
            }
        }

        return result;
    }

    /// <summary>
    /// Share (0..1) of the windows' time covered by valid samples; each valid sample covers the time to the next
    /// sample when that is at most <paramref name="maxSampleGap"/> away.
    /// </summary>
    public static double Coverage(TimeSeries series, IReadOnlyList<TimeRange> windows, TimeSpan maxSampleGap)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(windows);
        var total = windows.Sum(w => (double)w.DurationUs);
        if (total <= 0)
        {
            return 0;
        }

        var maxGapUs = (long)(maxSampleGap.TotalMilliseconds * 1000);
        double covered = 0;
        foreach (var w in windows)
        {
            for (var i = 0; i < series.Count - 1; i++)
            {
                if (!double.IsFinite(series.Values[i]))
                {
                    continue;
                }

                var a = series.Timestamps[i];
                var b = series.Timestamps[i + 1];
                if (b - a > maxGapUs)
                {
                    continue;
                }

                var from = Math.Max(a, w.StartUs);
                var to = Math.Min(b, w.EndUs);
                if (to > from)
                {
                    covered += to - from;
                }
            }
        }

        return Math.Clamp(covered / total, 0, 1);
    }

    /// <summary>Median distance between consecutive samples, in microseconds; null with fewer than two samples.</summary>
    public static long? MedianIntervalUs(long[] timestamps)
    {
        ArgumentNullException.ThrowIfNull(timestamps);
        if (timestamps.Length < 2)
        {
            return null;
        }

        var deltas = new long[timestamps.Length - 1];
        for (var i = 1; i < timestamps.Length; i++)
        {
            deltas[i - 1] = timestamps[i] - timestamps[i - 1];
        }

        Array.Sort(deltas);
        return deltas[deltas.Length / 2];
    }

    /// <summary>Gaps between consecutive samples longer than <paramref name="minGapUs"/>, clipped to the windows.</summary>
    public static List<TimeRange> FindGaps(long[] timestamps, IReadOnlyList<TimeRange> windows, long minGapUs)
    {
        ArgumentNullException.ThrowIfNull(timestamps);
        ArgumentNullException.ThrowIfNull(windows);
        var gaps = new List<TimeRange>();
        for (var i = 1; i < timestamps.Length; i++)
        {
            var a = timestamps[i - 1];
            var b = timestamps[i];
            if (b - a <= minGapUs)
            {
                continue;
            }

            foreach (var w in windows)
            {
                var from = Math.Max(a, w.StartUs);
                var to = Math.Min(b, w.EndUs);
                if (to - from > minGapUs)
                {
                    gaps.Add(new TimeRange(from, to));
                }
            }
        }

        return gaps;
    }

    private static bool InAny(IReadOnlyList<TimeRange> windows, long t)
    {
        for (var i = 0; i < windows.Count; i++)
        {
            if (windows[i].Contains(t))
            {
                return true;
            }
        }

        return false;
    }
}
