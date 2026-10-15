namespace FlightLog.Application.FlightData;

/// <summary>Reduces a time series for plotting while keeping extremes and gaps visible.</summary>
internal static class Downsampler
{
    /// <summary>
    /// Splits [from, to] into <c>maxPoints / 2</c> equal buckets and keeps, per bucket, the minimum and maximum valid
    /// sample in time order. A bucket that holds only invalid samples keeps one null at its first invalid sample,
    /// so that a gap stays a gap. Input must be sorted by time.
    /// </summary>
    public static (List<long> Timestamps, List<double?> Values) MinMax(IReadOnlyList<long> timestamps, IReadOnlyList<double> values, long from, long to, int maxPoints)
    {
        var bucketCount = Math.Max(1, maxPoints / 2);
        var width = Math.Max(1.0, (to - (double)from + 1) / bucketCount);
        var resultT = new List<long>(maxPoints);
        var resultV = new List<double?>(maxPoints);

        var i = 0;
        while (i < timestamps.Count)
        {
            var bucket = (long)((timestamps[i] - (double)from) / width);
            int minIndex = -1, maxIndex = -1, invalidIndex = -1;
            for (; i < timestamps.Count && (long)((timestamps[i] - (double)from) / width) == bucket; i++)
            {
                var v = values[i];
                if (!double.IsFinite(v))
                {
                    invalidIndex = invalidIndex < 0 ? i : invalidIndex;
                    continue;
                }

                if (minIndex < 0 || v < values[minIndex])
                {
                    minIndex = i;
                }

                if (maxIndex < 0 || v > values[maxIndex])
                {
                    maxIndex = i;
                }
            }

            if (minIndex < 0)
            {
                resultT.Add(timestamps[invalidIndex]);
                resultV.Add(null);
                continue;
            }

            var (first, second) = minIndex <= maxIndex ? (minIndex, maxIndex) : (maxIndex, minIndex);
            resultT.Add(timestamps[first]);
            resultV.Add(values[first]);
            if (second != first)
            {
                resultT.Add(timestamps[second]);
                resultV.Add(values[second]);
            }
        }

        return (resultT, resultV);
    }
}
