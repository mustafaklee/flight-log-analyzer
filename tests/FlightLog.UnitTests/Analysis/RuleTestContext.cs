using FlightLog.Application.Analysis;
using FlightLog.Domain.Common;
using FlightLog.Domain.FlightData;

namespace FlightLog.UnitTests.Analysis;

/// <summary>Builds analysis contexts from synthetic signals, events and parameters.</summary>
internal sealed class RuleTestContext : ISignalSource
{
    private readonly Dictionary<string, TimeSeries> _signals = new(StringComparer.Ordinal);
    private readonly List<FlightEvent> _events = [];
    private readonly List<FlightParameter> _parameters = [];

    public Autopilot Autopilot { get; set; } = Autopilot.ArduPilot;

    public string? VehicleType { get; set; } = "Copter";

    public TimeRange Log { get; set; } = new(0, 100_000_000);

    public IReadOnlyCollection<string> SignalNames => _signals.Keys;

    public static long S(double seconds) => (long)(seconds * 1_000_000);

    /// <summary>A signal sampled every <paramref name="periodS"/> seconds over the log, valued by <paramref name="value"/>(t in s).</summary>
    public RuleTestContext Signal(string name, Func<double, double> value, double periodS = 0.1, double fromS = 0, double? toS = null)
    {
        var to = toS ?? Log.EndUs / 1e6;
        var times = new List<long>();
        for (var i = 0; fromS + (i * periodS) <= to + 1e-9; i++)
        {
            times.Add((long)Math.Round((fromS + (i * periodS)) * 1_000_000));
        }

        _signals[name] = new TimeSeries(name, null, [.. times], [.. times.Select(t => value(t / 1e6))]);
        return this;
    }

    public RuleTestContext Signal(string name, long[] timestamps, double[] values)
    {
        _signals[name] = new TimeSeries(name, null, timestamps, values);
        return this;
    }

    public RuleTestContext Event(double atS, FlightEventKind kind, string text, string source = "ERR", EventSeverity? severity = null, Dictionary<string, string>? data = null)
    {
        _events.Add(new FlightEvent { Id = Guid.NewGuid(), FlightId = Guid.Empty, TimestampUs = S(atS), Kind = kind, Severity = severity, Source = source, Text = text, Data = data });
        return this;
    }

    public RuleTestContext Armed(double fromS, double toS) =>
        Event(fromS, FlightEventKind.Armed, "Armed", "ARM").Event(toS, FlightEventKind.Disarmed, "Disarmed", "ARM");

    public RuleTestContext Parameter(string name, double value, double? changedAtS = null)
    {
        _parameters.Add(new FlightParameter { Id = Guid.NewGuid(), FlightId = Guid.Empty, Name = name, Value = value, TimestampUs = changedAtS is { } s ? S(s) : null });
        return this;
    }

    public AnalysisContext Build() => new(Autopilot, VehicleType, Log, _events, _parameters, this);

    public Task<TimeSeries?> LoadAsync(string name, CancellationToken cancellationToken) => Task.FromResult(_signals.GetValueOrDefault(name));

    public async Task<RuleEvaluation> RunAsync(IAnalysisRule rule) => await rule.EvaluateAsync(Build(), TestContext.Current.CancellationToken);
}
