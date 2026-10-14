namespace FlightLog.ParserTests;

internal static class OracleAssertions
{
    public static void ShouldMatch(this AggregatingSink sink, ExpectedSeries[] expected, Func<ExpectedSeries, bool>? include = null)
    {
        include ??= _ => true;
        var expectedKeys = expected.Where(include).Select(s => (s.Message, s.Instance)).ToHashSet();
        var actualKeys = sink.Series.Keys.ToHashSet();
        expectedKeys.Except(actualKeys).Should().BeEmpty("every series the oracle found must be found");
        actualKeys.Except(expectedKeys).Should().BeEmpty("no series the oracle did not find may appear");

        foreach (var series in expected.Where(include))
        {
            var actual = sink.Series[(series.Message, series.Instance)];
            var label = $"{series.Message}[{series.Instance}]";
            actual.Records.Should().Be(series.Records, label);
            if (series.FirstTimestamp is { } first)
            {
                actual.FirstTimestamp.Should().Be(first, label);
                actual.LastTimestamp.Should().Be(series.LastTimestamp!.Value, label);
            }

            var numeric = actual.Series.Fields.Where(f => f.Kind == LogFieldKind.Numeric).Select(f => f.Name);
            numeric.Should().BeEquivalentTo(series.Fields.Keys, $"{label} numeric fields");
            foreach (var (field, agg) in series.Fields)
            {
                var column = actual.Column(field);
                var where = $"{label}.{field}";
                column.Count.Should().Be(agg.Count, where);
                column.Nonfinite.Should().Be(agg.Nonfinite, where);
                if (agg.Sum is null)
                {
                    continue;
                }

                column.Min.Should().Be(agg.Min!.Value, where);
                column.Max.Should().Be(agg.Max!.Value, where);
                var magnitude = Math.Max(Math.Abs(agg.Min.Value), Math.Abs(agg.Max.Value)) * agg.Count;
                column.CompensatedSum.Should().BeApproximately(agg.Sum.Value, Math.Max(magnitude * 1e-12, 1e-9), where);
            }
        }
    }
}
