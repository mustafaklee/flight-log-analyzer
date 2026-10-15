using FlightLog.Application.Abstractions;
using FlightLog.Application.Authorization;
using FlightLog.Application.Common;
using FlightLog.Domain.Common;
using FlightLog.Domain.FlightData;
using FlightLog.Domain.Flights;
using FlightLog.Domain.Teams;
using FlightLog.LogParsing.Signals;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace FlightLog.Application.FlightData;

/// <summary>Read access to what processing derived from a flight's log.</summary>
public interface IFlightDataService
{
    Task<IReadOnlyList<SignalDto>> ListSignalsAsync(Guid flightId, SignalQuery query, CancellationToken cancellationToken);

    Task<SignalDataDto> GetSignalDataAsync(Guid flightId, SignalDataQuery query, CancellationToken cancellationToken);

    Task<PagedResult<FlightEventDto>> ListEventsAsync(Guid flightId, EventQuery query, CancellationToken cancellationToken);

    Task<IReadOnlyList<FlightParameterDto>> ListParametersAsync(Guid flightId, ParameterQuery query, CancellationToken cancellationToken);

    Task<IReadOnlyList<ProcessingJobDto>> ListJobsAsync(Guid logFileId, CancellationToken cancellationToken);
}

internal sealed class FlightDataService(
    IApplicationDbContext db,
    ITeamAccessService access,
    IFileStorage storage,
    IValidator<SignalDataQuery> dataValidator,
    IValidator<EventQuery> eventValidator,
    IValidator<SignalQuery> signalValidator,
    IValidator<ParameterQuery> parameterValidator) : IFlightDataService
{
    /// <summary>Protects the API from requests that would read a whole long, high-rate log at once.</summary>
    public const int MaxChunksPerRequest = 2000;

    private const int MaxParallelReads = 8;

    public async Task<IReadOnlyList<SignalDto>> ListSignalsAsync(Guid flightId, SignalQuery query, CancellationToken cancellationToken)
    {
        await signalValidator.EnsureValidAsync(query, cancellationToken);
        await LoadAuthorizedFlightAsync(flightId, cancellationToken);

        var signals = db.FlightSignals.AsNoTracking().Where(s => s.FlightId == flightId);
        if (query.Kind is { } kind)
        {
            signals = signals.Where(s => s.Kind == kind);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim().ToLowerInvariant();
#pragma warning disable CA1304, CA1311, CA1862 // Translated to SQL lower(); culture overloads are not translatable.
            signals = signals.Where(s => s.Name.ToLower().Contains(term) || (s.Unit != null && s.Unit.ToLower() == term));
#pragma warning restore CA1304, CA1311, CA1862
        }

        var rows = await signals.OrderBy(s => s.Kind).ThenBy(s => s.Name).ToListAsync(cancellationToken);
        return [.. rows.Select(ToDto)];
    }

    public async Task<SignalDataDto> GetSignalDataAsync(Guid flightId, SignalDataQuery query, CancellationToken cancellationToken)
    {
        await dataValidator.EnsureValidAsync(query, cancellationToken);
        var flight = await LoadAuthorizedFlightAsync(flightId, cancellationToken);

        var names = query.Names.Distinct(StringComparer.Ordinal).ToList();
        var signals = await db.FlightSignals.AsNoTracking()
            .Where(s => s.FlightId == flightId && names.Contains(s.Name))
            .ToListAsync(cancellationToken);
        var missing = names.Except(signals.Select(s => s.Name), StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
        {
            throw new NotFoundException("Signal", string.Join(", ", missing));
        }

        var from = query.FromUs ?? long.MinValue;
        var to = query.ToUs ?? long.MaxValue;
        var seriesIds = signals.Select(s => s.SeriesId).Distinct().ToList();
        var series = await db.FlightSignalSeries.AsNoTracking().Where(s => seriesIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, cancellationToken);
        var chunks = await db.SignalChunks.AsNoTracking()
            .Where(c => seriesIds.Contains(c.SeriesId) && c.EndTimestampUs >= from && c.StartTimestampUs <= to)
            .OrderBy(c => c.SeriesId).ThenBy(c => c.Index)
            .ToListAsync(cancellationToken);
        if (chunks.Count > MaxChunksPerRequest)
        {
            throw new DomainRuleViolationException(
                "signal_data.range_too_large", "The requested time range holds too much data. Request fewer signals or a shorter range.");
        }

        // Read each chunk once and decode only the columns that were asked for.
        var columnsBySeries = signals.GroupBy(s => s.SeriesId).ToDictionary(g => g.Key, g => g.Select(s => s.Column).Distinct().Order().ToList());
        var decoded = new SignalChunkData[chunks.Count];
        await Parallel.ForAsync(
            0, chunks.Count, new ParallelOptions { MaxDegreeOfParallelism = MaxParallelReads, CancellationToken = cancellationToken }, async (i, ct) =>
            {
                var chunk = chunks[i];
                var bytes = await storage.ReadAllBytesAsync(series[chunk.SeriesId].ChunkKey(chunk.Index), ct);
                decoded[i] = SignalChunkCodec.Decode(bytes, columnsBySeries[chunk.SeriesId]);
            });

        var result = new List<SignalSeriesDataDto>();
        foreach (var name in names)
        {
            var signal = signals.Single(s => s.Name == name);
            var position = columnsBySeries[signal.SeriesId].IndexOf(signal.Column);
            var multiplier = signal.Multiplier ?? 1.0;
            var samples = new List<(long T, double V)>();
            for (var i = 0; i < chunks.Count; i++)
            {
                if (chunks[i].SeriesId != signal.SeriesId)
                {
                    continue;
                }

                var data = decoded[i];
                var column = data.Columns[position];
                for (var r = 0; r < data.Timestamps.Length; r++)
                {
                    var t = data.Timestamps[r];
                    if (t >= from && t <= to)
                    {
                        samples.Add((t, column[r] * multiplier));
                    }
                }
            }

            // Logs are written in time order, but a few messages arrive late; plot code expects sorted data.
            samples.Sort((a, b) => a.T.CompareTo(b.T));
            result.Add(ToData(signal, samples, query));
        }

        return new SignalDataDto(flight.Id, flight.LogStartTimestampUs, flight.BootTimeUtc, result);
    }

    public async Task<PagedResult<FlightEventDto>> ListEventsAsync(Guid flightId, EventQuery query, CancellationToken cancellationToken)
    {
        await eventValidator.EnsureValidAsync(query, cancellationToken);
        await LoadAuthorizedFlightAsync(flightId, cancellationToken);

        var events = db.FlightEvents.AsNoTracking().Where(e => e.FlightId == flightId);
        if (query.Kind is { } kind)
        {
            events = events.Where(e => e.Kind == kind);
        }

        if (query.MaxSeverity is { } severity)
        {
            // Lower numbers are more severe (syslog levels); events without severity are not filtered in.
            events = events.Where(e => e.Severity != null && e.Severity <= severity);
        }

        if (query.FromUs is { } from)
        {
            events = events.Where(e => e.TimestampUs >= from);
        }

        if (query.ToUs is { } to)
        {
            events = events.Where(e => e.TimestampUs <= to);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim().ToLowerInvariant();
#pragma warning disable CA1304, CA1311, CA1862 // Translated to SQL lower(); culture overloads are not translatable.
            events = events.Where(e => e.Text.ToLower().Contains(term));
#pragma warning restore CA1304, CA1311, CA1862
        }

        var pageSize = Math.Clamp(query.PageSize, 1, EventQuery.MaxPageSize);
        var page = Math.Max(1, query.Page);
        var total = await events.CountAsync(cancellationToken);
        var rows = await events.OrderBy(e => e.TimestampUs).ThenBy(e => e.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(cancellationToken);
        return new PagedResult<FlightEventDto>([.. rows.Select(e => new FlightEventDto(e.TimestampUs, e.Kind, e.Severity, e.Source, e.Text, e.Data))], page, pageSize, total);
    }

    public async Task<IReadOnlyList<FlightParameterDto>> ListParametersAsync(Guid flightId, ParameterQuery query, CancellationToken cancellationToken)
    {
        await parameterValidator.EnsureValidAsync(query, cancellationToken);
        await LoadAuthorizedFlightAsync(flightId, cancellationToken);

        var parameters = db.FlightParameters.AsNoTracking().Where(p => p.FlightId == flightId);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim().ToUpperInvariant();
#pragma warning disable CA1304, CA1311, CA1862 // Translated to SQL upper(); culture overloads are not translatable.
            parameters = parameters.Where(p => p.Name.ToUpper().Contains(term));
#pragma warning restore CA1304, CA1311, CA1862
        }

        var rows = await parameters.OrderBy(p => p.Name).ThenBy(p => p.TimestampUs).ToListAsync(cancellationToken);
        var result = rows.GroupBy(p => p.Name, StringComparer.Ordinal)
            .Select(g =>
            {
                var initial = g.FirstOrDefault(p => p.TimestampUs == null);
                var changes = g.Where(p => p.TimestampUs != null).OrderBy(p => p.TimestampUs)
                    .Select(p => new ParameterChangeDto(p.TimestampUs!.Value, p.Value)).ToList();
                return new FlightParameterDto(g.Key, initial?.Value, initial?.DefaultValue ?? g.Select(p => p.DefaultValue).FirstOrDefault(d => d != null), changes);
            });
        if (query.ChangedOnly)
        {
            result = result.Where(p => p.Changes.Count > 0);
        }

        return [.. result];
    }

    public async Task<IReadOnlyList<ProcessingJobDto>> ListJobsAsync(Guid logFileId, CancellationToken cancellationToken)
    {
        var teamId = await db.FlightLogFiles.AsNoTracking().Where(f => f.Id == logFileId).Select(f => (Guid?)f.TeamId).SingleOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException("LogFile", logFileId);
        await access.EnsureResourceRoleAsync(teamId, TeamRole.Viewer, "LogFile", logFileId, cancellationToken);

        var jobs = await db.LogProcessingJobs.AsNoTracking().Where(j => j.LogFileId == logFileId).OrderByDescending(j => j.StartedAt).ToListAsync(cancellationToken);
        return [.. jobs.Select(j => new ProcessingJobDto(
            j.Id, j.Attempt, j.Outcome, j.StartedAt, j.FinishedAt, j.DurationMs, j.AllocatedBytes, j.ParserName, j.ParserVersion,
            j.NormalizationVersion, j.MessageCount, j.RecordCount, j.SeriesCount, j.SignalCount, j.EventCount, j.ParameterCount,
            j.ChunkCount, j.ChunkBytes, j.SkippedBytes, j.Truncated, j.Corrupt, j.SummaryTruncated, j.Warnings, j.FailureCode,
            j.FailureMessage, j.CorrelationId))];
    }

    private static SignalSeriesDataDto ToData(FlightSignal signal, List<(long T, double V)> samples, SignalDataQuery query)
    {
        if (samples.Count <= query.MaxPoints)
        {
            return new SignalSeriesDataDto(
                signal.Name, signal.Unit, samples.Count, false,
                [.. samples.Select(s => s.T)], [.. samples.Select(s => double.IsFinite(s.V) ? s.V : (double?)null)]);
        }

        var (timestamps, values) = Downsampler.MinMax(
            samples.Select(s => s.T).ToList(), samples.Select(s => s.V).ToList(),
            query.FromUs ?? samples[0].T, query.ToUs ?? samples[^1].T, query.MaxPoints);
        return new SignalSeriesDataDto(signal.Name, signal.Unit, samples.Count, true, timestamps, values);
    }

    private static SignalDto ToDto(FlightSignal s)
    {
        var multiplier = s.Multiplier ?? 1.0;
        return new SignalDto(
            s.Name, s.Kind, s.Message, s.Instance, s.Field, s.Unit, s.Multiplier, s.SourceType,
            s.SourceMessage is null ? null : new SignalSourceDto(s.SourceMessage, s.SourceInstance ?? 0, s.SourceFields ?? "", s.Conversion ?? "", s.NormalizationVersion ?? ""),
            s.SampleCount, s.InvalidCount,
            Scale(s.Min, multiplier), Scale(s.Max, multiplier), s.FirstTimestampUs, s.LastTimestampUs);

        // A negative multiplier would swap min and max; DataFlash multipliers are positive powers of ten.
        static double? Scale(double? value, double multiplier) => value * multiplier;
    }

    private async Task<Flight> LoadAuthorizedFlightAsync(Guid flightId, CancellationToken cancellationToken)
    {
        var flight = await db.Flights.AsNoTracking().SingleOrDefaultAsync(f => f.Id == flightId, cancellationToken)
            ?? throw new NotFoundException("Flight", flightId);
        await access.EnsureResourceRoleAsync(flight.TeamId, TeamRole.Viewer, "Flight", flightId, cancellationToken);
        return flight;
    }
}

internal sealed class SignalDataQueryValidator : AbstractValidator<SignalDataQuery>
{
    public SignalDataQueryValidator()
    {
        RuleFor(x => x.Names).NotNull().NotEmpty().WithMessage("Give at least one signal name.")
            .Must(n => n is null || n.Count <= SignalDataQuery.MaxSignals).WithMessage($"At most {SignalDataQuery.MaxSignals} signals per request.");
        RuleForEach(x => x.Names).NotEmpty().MaximumLength(FlightSignal.NameMaxLength);
        RuleFor(x => x.MaxPoints).InclusiveBetween(10, SignalDataQuery.MaxMaxPoints);
        RuleFor(x => x.ToUs).GreaterThanOrEqualTo(x => x.FromUs).When(x => x.FromUs is not null && x.ToUs is not null)
            .WithMessage("'toUs' must not be earlier than 'fromUs'.");
    }
}

internal sealed class EventQueryValidator : AbstractValidator<EventQuery>
{
    public EventQueryValidator()
    {
        RuleFor(x => x.Kind).IsInEnum().When(x => x.Kind is not null);
        RuleFor(x => x.MaxSeverity).IsInEnum().When(x => x.MaxSeverity is not null);
        RuleFor(x => x.Search).MaximumLength(200);
        RuleFor(x => x.PageSize).InclusiveBetween(1, EventQuery.MaxPageSize);
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.ToUs).GreaterThanOrEqualTo(x => x.FromUs).When(x => x.FromUs is not null && x.ToUs is not null)
            .WithMessage("'toUs' must not be earlier than 'fromUs'.");
    }
}

internal sealed class SignalQueryValidator : AbstractValidator<SignalQuery>
{
    public SignalQueryValidator()
    {
        RuleFor(x => x.Kind).IsInEnum().When(x => x.Kind is not null);
        RuleFor(x => x.Search).MaximumLength(200);
    }
}

internal sealed class ParameterQueryValidator : AbstractValidator<ParameterQuery>
{
    public ParameterQueryValidator() => RuleFor(x => x.Search).MaximumLength(100);
}
