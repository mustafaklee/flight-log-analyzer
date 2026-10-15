using FlightLog.Application.Abstractions;
using FlightLog.Domain.FlightData;
using FlightLog.LogParsing.Signals;
using Microsoft.EntityFrameworkCore;

namespace FlightLog.Application.Analysis;

/// <summary>Reads a flight's signals from the chunk store at full resolution, for the analysis rules.</summary>
internal sealed class FlightSignalSource : ISignalSource
{
    private const int MaxParallelReads = 8;
    private readonly IApplicationDbContext _db;
    private readonly IFileStorage _storage;
    private readonly Dictionary<string, FlightSignal> _signals;

    private FlightSignalSource(IApplicationDbContext db, IFileStorage storage, Dictionary<string, FlightSignal> signals)
    {
        _db = db;
        _storage = storage;
        _signals = signals;
    }

    public IReadOnlyCollection<string> SignalNames => _signals.Keys;

    public static async Task<FlightSignalSource> CreateAsync(IApplicationDbContext db, IFileStorage storage, Guid flightId, CancellationToken cancellationToken)
    {
        var signals = await db.FlightSignals.AsNoTracking().Where(s => s.FlightId == flightId).ToDictionaryAsync(s => s.Name, StringComparer.Ordinal, cancellationToken);
        return new FlightSignalSource(db, storage, signals);
    }

    public async Task<TimeSeries?> LoadAsync(string name, CancellationToken cancellationToken)
    {
        if (!_signals.TryGetValue(name, out var signal))
        {
            return null;
        }

        var series = await _db.FlightSignalSeries.AsNoTracking().SingleAsync(s => s.Id == signal.SeriesId, cancellationToken);
        var chunks = await _db.SignalChunks.AsNoTracking().Where(c => c.SeriesId == signal.SeriesId).OrderBy(c => c.Index).ToListAsync(cancellationToken);
        var decoded = new SignalChunkData[chunks.Count];
        await Parallel.ForAsync(
            0, chunks.Count, new ParallelOptions { MaxDegreeOfParallelism = MaxParallelReads, CancellationToken = cancellationToken }, async (i, ct) =>
            {
                var bytes = await _storage.ReadAllBytesAsync(series.ChunkKey(chunks[i].Index), ct);
                decoded[i] = SignalChunkCodec.Decode(bytes, [signal.Column]);
            });

        var count = decoded.Sum(d => d.Timestamps.Length);
        var samples = new (long T, double V)[count];
        var n = 0;
        var multiplier = signal.Multiplier ?? 1.0;
        foreach (var data in decoded)
        {
            var column = data.Columns[0];
            for (var r = 0; r < data.Timestamps.Length; r++)
            {
                samples[n++] = (data.Timestamps[r], column[r] * multiplier);
            }
        }

        // Logs are written in time order, but a few messages arrive late.
        Array.Sort(samples, (a, b) => a.T.CompareTo(b.T));
        return new TimeSeries(name, signal.Unit, [.. samples.Select(s => s.T)], [.. samples.Select(s => s.V)]);
    }
}
