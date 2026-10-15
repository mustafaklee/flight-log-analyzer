using FlightLog.Application.Abstractions;
using FlightLog.Domain.FlightData;
using FlightLog.Domain.Flights;
using FlightLog.LogParsing;
using FlightLog.LogParsing.DataFlash;
using FlightLog.LogParsing.Interpretation;
using FlightLog.LogParsing.Normalization;
using FlightLog.LogParsing.Signals;
using FlightLog.LogParsing.ULog;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using DomainEventKind = FlightLog.Domain.FlightData.FlightEventKind;

namespace FlightLog.Application.Processing;

/// <summary>A file this worker has moved to Processing, together with the job that records the attempt.</summary>
public sealed record ClaimedJob(Guid JobId, Guid LogFileId, Guid FlightId, string CorrelationId);

public interface ILogProcessingService
{
    /// <summary>
    /// Moves the oldest queued file to Processing and starts a job for it. Returns null when nothing is queued,
    /// and a result without job when another worker claimed the same file first.
    /// </summary>
    Task<ClaimResult?> ClaimNextAsync(string workerId, CancellationToken cancellationToken);

    /// <summary>
    /// Parses the file, stores signal chunks and the catalog, and ends the job. <paramref name="cancellation"/>
    /// fires when the user cancels the file or the worker shuts down; <paramref name="shutdown"/> tells which.
    /// Never throws for problems of the file itself: they end as a failed job with a stable code.
    /// </summary>
    Task ProcessAsync(ClaimedJob claim, CancellationToken cancellation, CancellationToken shutdown);

    /// <summary>Fails files that stayed in Processing longer than allowed (their worker died).</summary>
    Task<int> RecoverStaleAsync(CancellationToken cancellationToken);
}

public sealed record ClaimResult(ClaimedJob? Job);

internal sealed partial class LogProcessingService(
    IApplicationDbContext db,
    IFileStorage storage,
    TimeProvider clock,
    IOptions<ProcessingOptions> options,
    ILogger<LogProcessingService> logger) : ILogProcessingService
{
    private const string ChunkContentType = "application/vnd.flightlog.signal-chunk";

    public async Task<ClaimResult?> ClaimNextAsync(string workerId, CancellationToken cancellationToken)
    {
        var candidate = await db.FlightLogFiles.AsNoTracking()
            .Where(f => f.Status == ProcessingStatus.Queued)
            .OrderBy(f => f.StatusChangedAt).ThenBy(f => f.Id)
            .Select(f => (Guid?)f.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (candidate is not { } fileId)
        {
            return null;
        }

        var file = await db.FlightLogFiles.SingleAsync(f => f.Id == fileId, cancellationToken);
        if (file.Status != ProcessingStatus.Queued)
        {
            return new ClaimResult(null);
        }

        var now = clock.GetUtcNow();
        var attempt = await db.LogProcessingJobs.CountAsync(j => j.LogFileId == fileId, cancellationToken) + 1;
        var job = LogProcessingJob.Start(file.TeamId, file.Id, file.FlightId, attempt, workerId, Guid.NewGuid().ToString("N"), now);
        file.TransitionTo(ProcessingStatus.Processing, now);
        db.LogProcessingJobs.Add(job);
        try
        {
            // The row version of the file makes this a compare-and-set: only one worker can win the claim.
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return new ClaimResult(null);
        }

        return new ClaimResult(new ClaimedJob(job.Id, file.Id, file.FlightId, job.CorrelationId));
    }

    public async Task ProcessAsync(ClaimedJob claim, CancellationToken cancellation, CancellationToken shutdown)
    {
        ArgumentNullException.ThrowIfNull(claim);
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = claim.CorrelationId, ["LogFileId"] = claim.LogFileId });
        var allocatedAtStart = GC.GetTotalAllocatedBytes();
        var tempRoot = string.IsNullOrWhiteSpace(options.Value.TempDirectory) ? Path.GetTempPath() : options.Value.TempDirectory;
        var workDirectory = Path.Combine(tempRoot, $"flightlog-job-{claim.JobId:N}");
        Directory.CreateDirectory(workDirectory);
        var job = await db.LogProcessingJobs.SingleAsync(j => j.Id == claim.JobId, CancellationToken.None);
        LogStarted(claim.LogFileId, job.Attempt);

        try
        {
            var file = await db.FlightLogFiles.SingleAsync(f => f.Id == claim.LogFileId, cancellation);
            var flight = await db.Flights.SingleAsync(f => f.Id == claim.FlightId, cancellation);
            var result = await ParseAsync(file, workDirectory, cancellation);
            var series = await UploadChunksAsync(result, job, cancellation);
            await SaveResultsAsync(file, flight, job, result, series, allocatedAtStart, cancellation);
            LogSucceeded(claim.LogFileId, job.DurationMs, job.RecordCount, job.SignalCount, job.ChunkBytes);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            await EndWithoutResultAsync(claim, null, null, shutdown.IsCancellationRequested, allocatedAtStart);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The file changed while the results were saved: the user cancelled it.
            await EndWithoutResultAsync(claim, null, null, false, allocatedAtStart);
        }
        catch (LogParseException ex)
        {
            LogParseFailed(ex, claim.LogFileId, ex.Code);
            await EndWithoutResultAsync(claim, ex.Code, ex.Message, false, allocatedAtStart);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogUnexpectedFailure(ex, claim.LogFileId);
            await EndWithoutResultAsync(
                claim, "processing.failed",
                $"The file could not be processed because of an internal error (correlation id {claim.CorrelationId}).", false, allocatedAtStart);
        }
        finally
        {
            TryDeleteDirectory(workDirectory);
        }
    }

    public async Task<int> RecoverStaleAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var threshold = now - options.Value.StaleAfter;
        var stale = await db.FlightLogFiles
            .Where(f => f.Status == ProcessingStatus.Processing && f.StatusChangedAt < threshold)
            .ToListAsync(cancellationToken);
        if (stale.Count == 0)
        {
            return 0;
        }

        var ids = stale.Select(f => f.Id).ToList();
        var jobs = await db.LogProcessingJobs.Where(j => ids.Contains(j.LogFileId) && j.Outcome == ProcessingJobOutcome.Running).ToListAsync(cancellationToken);
        const string Code = "processing.interrupted";
        const string Message = "Processing stopped unexpectedly. Retry the file.";
        foreach (var file in stale)
        {
            file.Fail(Code, Message, now);
        }

        foreach (var job in jobs)
        {
            job.Fail(Code, Message, 0, now);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another worker finished or recovered one of them in the meantime; the next round tries again.
            db.ChangeTracker.Clear();
            return 0;
        }

        foreach (var job in jobs)
        {
            await TryDeletePrefixAsync(job.StoragePrefix);
        }

        LogRecovered(stale.Count);
        return stale.Count;
    }

    private async Task<ParseResult> ParseAsync(FlightLogFile file, string workDirectory, CancellationToken cancellationToken)
    {
        var rawPath = Path.Combine(workDirectory, "raw");
        await using (var source = await storage.OpenReadAsync(file.StorageKey, cancellationToken))
        await using (var target = new FileStream(rawPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
        {
            await source.CopyToAsync(target, cancellationToken);
        }

        var chunkDirectory = Directory.CreateDirectory(Path.Combine(workDirectory, "chunks")).FullName;
        var chunks = new List<StagedChunk>();
        var chunkSink = new SignalChunkSink(chunk =>
        {
            var path = Path.Combine(chunkDirectory, $"{chunk.SeriesId}-{chunk.Index}.fls");
            File.WriteAllBytes(path, chunk.Data);
            chunks.Add(new StagedChunk(chunk.SeriesId, chunk.Index, chunk.StartTimestampUs, chunk.EndTimestampUs, chunk.RecordCount, chunk.Data.Length, path));
        });

        (IFlightLogParser parser, FlightInterpreter interpreter, Func<ILogRecordSink, SignalNormalizer> normalizer) = file.Format switch
        {
            LogFormat.Px4ULog => ((IFlightLogParser)new ULogParser(), (FlightInterpreter)new Px4Interpreter(), (Func<ILogRecordSink, SignalNormalizer>)SignalNormalizer.ForPx4),
            LogFormat.ArduPilotDataFlash => (new DataFlashParser(), new ArduPilotInterpreter(), SignalNormalizer.ForArduPilot),
            _ => throw new LogParseException("processing.unsupported_format", $"Format {file.Format} cannot be processed."),
        };

        var sink = normalizer(new CompositeSink(chunkSink, interpreter));
        var limits = new ParserLimits { Timeout = options.Value.ParserTimeout };
        var report = await Task.Run(
            () =>
            {
                using var stream = new FileStream(rawPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
                var parsed = parser.Parse(stream, sink, limits, cancellationToken);
                chunkSink.Complete();
                return parsed;
            },
            cancellationToken);

        if (report.RecordCount == 0)
        {
            throw new LogParseException("processing.no_data", "The log contains no data records.");
        }

        return new ParseResult(report, interpreter.Complete(report), chunkSink, sink, chunks);
    }

    private async Task<Dictionary<int, FlightSignalSeries>> UploadChunksAsync(ParseResult result, LogProcessingJob job, CancellationToken cancellationToken)
    {
        var series = new Dictionary<int, FlightSignalSeries>();
        var ordinal = 0;
        foreach (var stats in result.Chunks.Series)
        {
            series[stats.Series.Id] = new FlightSignalSeries
            {
                Id = Guid.CreateVersion7(),
                FlightId = job.FlightId,
                JobId = job.Id,
                Message = stats.Series.Message,
                Instance = stats.Series.Instance,
                Kind = SignalNormalizer.IsNormalized(stats.Series) ? SignalKind.Normalized : SignalKind.Raw,
                RecordCount = stats.RecordCount,
                FirstTimestampUs = stats.FirstTimestampUs,
                LastTimestampUs = stats.LastTimestampUs,
                ColumnCount = stats.Fields.Count,
                StoragePrefix = $"{job.StoragePrefix}{ordinal++:D5}/",
            };
        }

        await Parallel.ForEachAsync(
            result.StagedChunks,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.Value.MaxParallelUploads), CancellationToken = cancellationToken },
            async (chunk, ct) =>
            {
                var bytes = await File.ReadAllBytesAsync(chunk.Path, ct);
                await storage.UploadBytesAsync(series[chunk.SeriesId].ChunkKey(chunk.Index), bytes, ChunkContentType, ct);
            });
        return series;
    }

    private async Task SaveResultsAsync(
        FlightLogFile file, Flight flight, LogProcessingJob job, ParseResult result, Dictionary<int, FlightSignalSeries> series, long allocatedAtStart, CancellationToken cancellationToken)
    {
        var summary = result.Summary;
        var signals = BuildSignals(result, series);
        var chunks = result.StagedChunks.Select(c => new SignalChunkInfo
        {
            SeriesId = series[c.SeriesId].Id,
            Index = c.Index,
            StartTimestampUs = c.StartTimestampUs,
            EndTimestampUs = c.EndTimestampUs,
            RecordCount = c.RecordCount,
            SizeBytes = c.SizeBytes,
        }).ToList();
        var events = summary.Events.Select(e => new FlightEvent
        {
            Id = Guid.CreateVersion7(),
            FlightId = flight.Id,
            TimestampUs = e.TimestampUs,
            Kind = Enum.Parse<DomainEventKind>(e.Kind.ToString()),
            Severity = e.Severity is { } severity ? (EventSeverity)(int)severity : null,
            Source = Shorten(e.Source, 200),
            Text = Shorten(e.Text, 1000),
            Data = e.Data,
        }).ToList();
        var parameters = summary.Parameters.Where(p => p.Name.Length is > 0 and <= 100).Select(p => new FlightParameter
        {
            Id = Guid.CreateVersion7(),
            FlightId = flight.Id,
            Name = p.Name,
            Value = p.Value,
            DefaultValue = p.DefaultValue,
            TimestampUs = p.TimestampUs,
        }).ToList();

        var report = result.Report;
        var now = clock.GetUtcNow();
        var previousPrefixes = await db.LogProcessingJobs
            .Where(j => j.FlightId == flight.Id && j.Id != job.Id)
            .Select(j => j.StoragePrefix)
            .ToListAsync(cancellationToken);

        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            // Reprocessing replaces everything derived from the earlier run.
            await db.FlightSignalSeries.Where(s => s.FlightId == flight.Id).ExecuteDeleteAsync(cancellationToken);
            await db.FlightEvents.Where(e => e.FlightId == flight.Id).ExecuteDeleteAsync(cancellationToken);
            await db.FlightParameters.Where(p => p.FlightId == flight.Id).ExecuteDeleteAsync(cancellationToken);

            db.FlightSignalSeries.AddRange(series.Values);
            db.SignalChunks.AddRange(chunks);
            db.FlightSignals.AddRange(signals);
            db.FlightEvents.AddRange(events);
            db.FlightParameters.AddRange(parameters);

            flight.RecordLogDetails(
                new FlightLogDetails
                {
                    StartedAt = summary.StartedAtUtc,
                    DurationUs = summary.DurationUs is >= 0 ? summary.DurationUs : null,
                    FirmwareVersion = summary.FirmwareVersion,
                    VehicleType = summary.VehicleType,
                    Hardware = summary.Hardware,
                    BootTimeUtc = summary.BootTimeUtc,
                    LogStartTimestampUs = summary.FirstTimestampUs,
                    Metadata = summary.Metadata,
                },
                now);
            file.TransitionTo(ProcessingStatus.Processed, now);
            job.Succeed(
                new ProcessingStatistics
                {
                    ParserName = report.Parser,
                    ParserVersion = report.ParserVersion,
                    NormalizationVersion = SignalNormalizer.NormalizationVersion,
                    MessageCount = report.MessageCount,
                    RecordCount = report.RecordCount,
                    SeriesCount = series.Count,
                    SignalCount = signals.Count,
                    EventCount = events.Count,
                    ParameterCount = parameters.Count,
                    ChunkCount = chunks.Count,
                    ChunkBytes = result.Chunks.ChunkBytes,
                    SkippedBytes = report.SkippedBytes,
                    Truncated = report.Truncated,
                    Corrupt = report.Corrupt,
                    SummaryTruncated = summary.Truncated,
                    Warnings = report.Warnings,
                },
                GC.GetTotalAllocatedBytes() - allocatedAtStart,
                now);

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }

        foreach (var prefix in previousPrefixes)
        {
            await TryDeletePrefixAsync(prefix);
        }
    }

    private static List<FlightSignal> BuildSignals(ParseResult result, Dictionary<int, FlightSignalSeries> series)
    {
        var signals = new List<FlightSignal>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var stats in result.Chunks.Series)
        {
            var stored = series[stats.Series.Id];
            var normalized = stored.Kind == SignalKind.Normalized;
            foreach (var field in stats.Fields)
            {
                var name = normalized
                    ? $"{stats.Series.Message[SignalNormalizer.MessagePrefix.Length..]}.{field.Field.Name}"
                    : $"{stats.Series.Message}{(stats.Series.Instance == 0 ? "" : $"#{stats.Series.Instance}")}.{field.Field.Name}";

                // A DataFlash log may redefine a message with another layout: keep both, distinguishable.
                var unique = name;
                for (var n = 2; !names.Add(unique); n++)
                {
                    unique = $"{name}~{n}";
                }

                if (unique.Length > FlightSignal.NameMaxLength)
                {
                    continue;
                }

                var source = normalized ? result.Normalizer.SourceOf(field.Field) : null;
                signals.Add(new FlightSignal
                {
                    Id = Guid.CreateVersion7(),
                    FlightId = stored.FlightId,
                    SeriesId = stored.Id,
                    Name = unique,
                    Kind = stored.Kind,
                    Message = stats.Series.Message,
                    Instance = stats.Series.Instance,
                    Field = field.Field.Name,
                    Column = field.Column,
                    SourceType = Shorten(field.Field.TypeName, 50),
                    Unit = string.IsNullOrEmpty(field.Field.Unit) ? null : Shorten(field.Field.Unit, 50),
                    Multiplier = field.Field.Multiplier,
                    SourceMessage = source?.Message,
                    SourceInstance = source?.Instance,
                    SourceFields = source?.Fields,
                    Conversion = source?.Conversion,
                    NormalizationVersion = normalized ? SignalNormalizer.NormalizationVersion : null,
                    SampleCount = field.SampleCount,
                    InvalidCount = field.InvalidCount,
                    Min = field.Min,
                    Max = field.Max,
                    FirstTimestampUs = stats.FirstTimestampUs,
                    LastTimestampUs = stats.LastTimestampUs,
                });
            }
        }

        return signals;
    }

    /// <summary>Ends a job that produced no result: user cancellation, worker shutdown or failure.</summary>
    private async Task EndWithoutResultAsync(ClaimedJob claim, string? failureCode, string? failureMessage, bool shutdown, long allocatedAtStart)
    {
        // Runs after the job token fired, so it must not use it.
        var token = CancellationToken.None;
        db.ChangeTracker.Clear();
        var file = await db.FlightLogFiles.SingleOrDefaultAsync(f => f.Id == claim.LogFileId, token);
        var job = await db.LogProcessingJobs.SingleOrDefaultAsync(j => j.Id == claim.JobId, token);
        var now = clock.GetUtcNow();
        var allocated = GC.GetTotalAllocatedBytes() - allocatedAtStart;

        if (job is not null && job.Outcome == ProcessingJobOutcome.Running)
        {
            if (failureCode is not null && file?.Status == ProcessingStatus.Processing)
            {
                job.Fail(failureCode, failureMessage!, allocated, now);
                file.Fail(failureCode, failureMessage!, now);
            }
            else
            {
                job.Cancel(allocated, now);
                if (shutdown && file?.Status == ProcessingStatus.Processing)
                {
                    file.TransitionTo(ProcessingStatus.Queued, now);
                }
            }

            try
            {
                await db.SaveChangesAsync(token);
            }
            catch (DbUpdateConcurrencyException)
            {
                // The user changed the file at the same moment; the job outcome is recorded on the next recovery.
                db.ChangeTracker.Clear();
            }

            LogEnded(claim.LogFileId, job.Outcome, failureCode);
        }

        // Also when the flight (and with it the job) was deleted while it was processed.
        await TryDeletePrefixAsync(LogProcessingJob.StoragePrefixFor(claim.FlightId, claim.JobId));
    }

    private async Task TryDeletePrefixAsync(string prefix)
    {
        try
        {
            await storage.DeletePrefixAsync(prefix, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogCleanupFailed(ex, prefix);
        }
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException ex)
        {
            LogCleanupFailed(ex, path);
        }
        catch (UnauthorizedAccessException ex)
        {
            LogCleanupFailed(ex, path);
        }
    }

    private static string Shorten(string value, int maxLength) => value.Length > maxLength ? value[..maxLength] : value;

    private sealed record StagedChunk(int SeriesId, int Index, long StartTimestampUs, long EndTimestampUs, int RecordCount, int SizeBytes, string Path);

    private sealed record ParseResult(ParserReport Report, FlightSummary Summary, SignalChunkSink Chunks, SignalNormalizer Normalizer, List<StagedChunk> StagedChunks);

    [LoggerMessage(Level = LogLevel.Information, Message = "Processing log file {LogFileId} (attempt {Attempt})")]
    private partial void LogStarted(Guid logFileId, int attempt);

    [LoggerMessage(Level = LogLevel.Information, Message = "Processed log file {LogFileId} in {DurationMs} ms: {Records} records, {Signals} signals, {ChunkBytes} chunk bytes")]
    private partial void LogSucceeded(Guid logFileId, long? durationMs, long records, int signals, long chunkBytes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Log file {LogFileId} was rejected by the parser: {Code}")]
    private partial void LogParseFailed(Exception exception, Guid logFileId, string code);

    [LoggerMessage(Level = LogLevel.Error, Message = "Processing log file {LogFileId} failed unexpectedly")]
    private partial void LogUnexpectedFailure(Exception exception, Guid logFileId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Processing of log file {LogFileId} ended as {Outcome} ({Code})")]
    private partial void LogEnded(Guid logFileId, ProcessingJobOutcome outcome, string? code);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Marked {Count} abandoned log files as failed")]
    private partial void LogRecovered(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not clean up {Location}")]
    private partial void LogCleanupFailed(Exception exception, string location);
}
