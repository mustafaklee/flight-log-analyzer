using FlightLog.Domain.Common;
using FlightLog.Domain.FlightData;

namespace FlightLog.Application.Analysis;

/// <summary>Loads signals of the analyzed flight at full resolution.</summary>
public interface ISignalSource
{
    /// <summary>Names of all signals of the flight.</summary>
    IReadOnlyCollection<string> SignalNames { get; }

    /// <summary>The signal, or null when the flight has no signal of that name.</summary>
    Task<TimeSeries?> LoadAsync(string name, CancellationToken cancellationToken);
}

/// <summary>Everything a rule may look at. Signals are loaded on demand and cached for the run.</summary>
public sealed class AnalysisContext
{
    private readonly ISignalSource _signals;
    private readonly Dictionary<string, TimeSeries?> _cache = new(StringComparer.Ordinal);

    public AnalysisContext(
        Autopilot autopilot, string? vehicleType, TimeRange log, IReadOnlyList<FlightEvent> events, IReadOnlyList<FlightParameter> parameters, ISignalSource signals)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(parameters);
        Autopilot = autopilot;
        VehicleType = vehicleType;
        Log = log;
        Events = [.. events.OrderBy(e => e.TimestampUs)];
        _signals = signals ?? throw new ArgumentNullException(nameof(signals));
        InitialParameters = parameters.Where(p => p.TimestampUs is null)
            .GroupBy(p => p.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal);
        ParameterChanges = [.. parameters.Where(p => p.TimestampUs is not null).OrderBy(p => p.TimestampUs)];
        ArmedIntervals = BuildArmedIntervals(Events, log);
        Windows = ArmedIntervals.Count > 0 ? ArmedIntervals : [log];
    }

    public Autopilot Autopilot { get; }

    public string? VehicleType { get; }

    /// <summary>The whole log (first to last timestamp).</summary>
    public TimeRange Log { get; }

    public IReadOnlyList<FlightEvent> Events { get; }

    /// <summary>Parameter values at log start.</summary>
    public IReadOnlyDictionary<string, double> InitialParameters { get; }

    /// <summary>Parameter changes during the log, in time order.</summary>
    public IReadOnlyList<FlightParameter> ParameterChanges { get; }

    /// <summary>Armed periods derived from arm/disarm events; empty when the log has none.</summary>
    public IReadOnlyList<TimeRange> ArmedIntervals { get; }

    public bool HasArmingInformation => ArmedIntervals.Count > 0;

    /// <summary>What flight-time rules analyze: the armed periods, or the whole log when arming is unknown.</summary>
    public IReadOnlyList<TimeRange> Windows { get; }

    /// <summary>Limitation text to attach when <see cref="Windows"/> had to fall back to the whole log.</summary>
    public string? WindowLimitation => HasArmingInformation
        ? null
        : "The log has no arm/disarm events, so the whole log was analyzed, including time on the ground.";

    public IReadOnlyCollection<string> SignalNames => _signals.SignalNames;

    public bool HasSignal(string name) => _signals.SignalNames.Contains(name);

    public async Task<TimeSeries?> SignalAsync(string name, CancellationToken cancellationToken)
    {
        if (!_cache.TryGetValue(name, out var series))
        {
            series = HasSignal(name) ? await _signals.LoadAsync(name, cancellationToken) : null;
            _cache[name] = series;
        }

        return series;
    }

    /// <summary>The value of the parameter at log start, or null when the log does not contain it.</summary>
    public double? Parameter(string name) => InitialParameters.TryGetValue(name, out var value) ? value : null;

    private static List<TimeRange> BuildArmedIntervals(IReadOnlyList<FlightEvent> events, TimeRange log)
    {
        var result = new List<TimeRange>();
        long? armedAt = null;

        // A log that starts while armed (logging began after arming) shows a disarm without an arm before it.
        var first = events.FirstOrDefault(e => e.Kind is FlightEventKind.Armed or FlightEventKind.Disarmed);
        if (first?.Kind == FlightEventKind.Disarmed)
        {
            armedAt = log.StartUs;
        }

        foreach (var e in events)
        {
            if (e.Kind == FlightEventKind.Armed && armedAt is null)
            {
                armedAt = e.TimestampUs;
            }
            else if (e.Kind == FlightEventKind.Disarmed && armedAt is { } start)
            {
                if (e.TimestampUs > start)
                {
                    result.Add(new TimeRange(start, e.TimestampUs));
                }

                armedAt = null;
            }
        }

        // Still armed when the log ends (power loss, crash or a log that was cut).
        if (armedAt is { } open && log.EndUs > open)
        {
            result.Add(new TimeRange(open, log.EndUs));
        }

        return result;
    }
}
