using System.Security.Cryptography;
using FlightLog.Application.Abstractions;
using FlightLog.Application.Authorization;
using FlightLog.Application.Common;
using FlightLog.Application.Flights;
using FlightLog.Domain.Common;
using FlightLog.Domain.Flights;
using FlightLog.Domain.Teams;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FlightLog.Application.Logs;

public interface ILogFileService
{
    /// <summary>
    /// Stores an uploaded log and creates its flight. The content is staged to a temporary file while its
    /// size and SHA-256 are computed and its format is checked; nothing is parsed during the request.
    /// </summary>
    Task<UploadLogResult> UploadAsync(Guid projectId, UploadLogRequest request, Stream content, CancellationToken cancellationToken);

    Task<LogFileDto> GetAsync(Guid logFileId, CancellationToken cancellationToken);

    Task<LogFileContent> OpenContentAsync(Guid logFileId, CancellationToken cancellationToken);

    Task<LogFileDto> CancelAsync(Guid logFileId, CancellationToken cancellationToken);

    Task<LogFileDto> RetryAsync(Guid logFileId, CancellationToken cancellationToken);
}

internal sealed class LogFileService(
    IApplicationDbContext db,
    ITeamAccessService access,
    ICurrentUser currentUser,
    IFileStorage storage,
    TimeProvider clock,
    IOptions<UploadOptions> options) : ILogFileService
{
    private const string ContentType = "application/octet-stream";

    public async Task<UploadLogResult> UploadAsync(Guid projectId, UploadLogRequest request, Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(content);

        var project = await db.Projects.AsNoTracking().SingleOrDefaultAsync(p => p.Id == projectId, cancellationToken)
            ?? throw new NotFoundException("Project", projectId);
        await access.EnsureResourceRoleAsync(project.TeamId, TeamRole.Engineer, "Project", projectId, cancellationToken);

        if (request.AircraftId is { } aircraftId)
        {
            await FlightService.EnsureAircraftInTeamAsync(db, project.TeamId, aircraftId, cancellationToken);
        }

        var fileName = FileNames.Sanitize(request.FileName);
        var claimedFormat = LogFormatDetector.FromExtension(fileName)
            ?? throw new DomainRuleViolationException("log_file.unsupported_extension", "Only PX4 ULog (.ulg) and ArduPilot DataFlash (.bin) files are supported.");

        var staged = await StageAsync(content, cancellationToken);
        try
        {
            var detected = LogFormatDetector.Detect(staged.Header)
                ?? throw new DomainRuleViolationException("log_file.unrecognized_content", "The file content is not a PX4 ULog or ArduPilot DataFlash log.");
            if (detected != claimedFormat)
            {
                throw new DomainRuleViolationException(
                    "log_file.extension_mismatch",
                    $"The file extension says {claimedFormat} but the content is {detected}.");
            }

            await EnsureNotAlreadyUploadedAsync(project.TeamId, staged.Sha256, cancellationToken);

            // Raw files are content addressed, so identical uploads (also across teams) share one object
            // while ownership stays in the separate per-team database records.
            var storageKey = $"raw/{staged.Sha256}";
            if (!await storage.ExistsAsync(storageKey, cancellationToken))
            {
                await storage.UploadFileAsync(storageKey, staged.Path, ContentType, cancellationToken);
            }

            var now = clock.GetUtcNow();
            var flightName = string.IsNullOrWhiteSpace(request.FlightName) ? Path.GetFileNameWithoutExtension(fileName) : request.FlightName;
            if (string.IsNullOrWhiteSpace(flightName))
            {
                flightName = fileName;
            }

            var flight = Flight.Create(project.TeamId, project.Id, request.AircraftId, flightName, detected.ToAutopilot(), currentUser.UserId, now);
            var logFile = FlightLogFile.Create(project.TeamId, flight.Id, fileName, staged.Size, staged.Sha256, detected, storageKey, currentUser.UserId, now);
            logFile.TransitionTo(ProcessingStatus.Validating, now);
            logFile.TransitionTo(ProcessingStatus.Queued, now);

            db.Flights.Add(flight);
            db.FlightLogFiles.Add(logFile);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // A concurrent upload of the same content may have won the unique (team, sha256) race.
                db.Flights.Entry(flight).State = EntityState.Detached;
                db.FlightLogFiles.Entry(logFile).State = EntityState.Detached;
                await EnsureNotAlreadyUploadedAsync(project.TeamId, staged.Sha256, cancellationToken);
                throw;
            }

            var aircraftName = request.AircraftId is null
                ? null
                : await db.Aircraft.Where(a => a.Id == request.AircraftId).Select(a => a.Name).SingleOrDefaultAsync(cancellationToken);
            return new UploadLogResult(FlightMapping.ToDto(flight, project.Name, aircraftName, logFile), FlightMapping.ToDto(logFile));
        }
        finally
        {
            File.Delete(staged.Path);
        }
    }

    public async Task<LogFileDto> GetAsync(Guid logFileId, CancellationToken cancellationToken) =>
        FlightMapping.ToDto(await LoadAuthorizedAsync(logFileId, TeamRole.Viewer, cancellationToken));

    public async Task<LogFileContent> OpenContentAsync(Guid logFileId, CancellationToken cancellationToken)
    {
        var file = await LoadAuthorizedAsync(logFileId, TeamRole.Viewer, cancellationToken);
        var stream = await storage.OpenReadAsync(file.StorageKey, cancellationToken);
        return new LogFileContent(stream, file.OriginalFileName, file.SizeBytes);
    }

    public async Task<LogFileDto> CancelAsync(Guid logFileId, CancellationToken cancellationToken)
    {
        var file = await LoadAuthorizedAsync(logFileId, TeamRole.Engineer, cancellationToken);
        file.Cancel(clock.GetUtcNow());
        await SaveStatusChangeAsync(cancellationToken);
        return FlightMapping.ToDto(file);
    }

    public async Task<LogFileDto> RetryAsync(Guid logFileId, CancellationToken cancellationToken)
    {
        var file = await LoadAuthorizedAsync(logFileId, TeamRole.Engineer, cancellationToken);
        file.Retry(clock.GetUtcNow());
        await SaveStatusChangeAsync(cancellationToken);
        return FlightMapping.ToDto(file);
    }

    private async Task SaveStatusChangeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("log_file.status_changed", "The file status changed in the meantime. Reload and try again.");
        }
    }

    private async Task<FlightLogFile> LoadAuthorizedAsync(Guid logFileId, TeamRole required, CancellationToken cancellationToken)
    {
        var file = await db.FlightLogFiles.SingleOrDefaultAsync(l => l.Id == logFileId, cancellationToken)
            ?? throw new NotFoundException("LogFile", logFileId);
        await access.EnsureResourceRoleAsync(file.TeamId, required, "LogFile", logFileId, cancellationToken);
        return file;
    }

    private async Task EnsureNotAlreadyUploadedAsync(Guid teamId, string sha256, CancellationToken cancellationToken)
    {
        var existing = await db.FlightLogFiles.AsNoTracking()
            .Where(l => l.TeamId == teamId && l.Sha256 == sha256)
            .Select(l => new { l.Id, l.FlightId })
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            throw new ConflictException(
                "log_file.duplicate",
                "This log file has already been uploaded to the team.",
                new Dictionary<string, object?> { ["flightId"] = existing.FlightId, ["logFileId"] = existing.Id });
        }
    }

    private async Task<StagedFile> StageAsync(Stream content, CancellationToken cancellationToken)
    {
        var limit = options.Value.MaxFileSizeBytes;
        var directory = string.IsNullOrWhiteSpace(options.Value.TempDirectory) ? Path.GetTempPath() : options.Value.TempDirectory;
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"flightlog-upload-{Guid.NewGuid():N}.tmp");

        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var header = new byte[LogFormatDetector.HeaderLength];
            var headerLength = 0;
            long size = 0;
            var buffer = new byte[81920];

            await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                int read;
                while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    size += read;
                    if (size > limit)
                    {
                        throw new PayloadTooLargeException(limit);
                    }

                    if (headerLength < header.Length)
                    {
                        var take = Math.Min(read, header.Length - headerLength);
                        Array.Copy(buffer, 0, header, headerLength, take);
                        headerLength += take;
                    }

                    hash.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }

            if (size == 0)
            {
                throw new DomainRuleViolationException("log_file.empty", "The uploaded file is empty.");
            }

            return new StagedFile(path, size, Convert.ToHexStringLower(hash.GetHashAndReset()), header[..headerLength]);
        }
        catch
        {
            File.Delete(path);
            throw;
        }
    }

    private sealed record StagedFile(string Path, long Size, string Sha256, byte[] Header);
}
