using FlightLog.Application.Analysis;
using FlightLog.Domain.Common;
using FlightLog.Domain.Flights;

namespace FlightLog.Application.Flights;

public enum FlightSort
{
    OccurredAtDesc = 0,
    OccurredAtAsc,
    NameAsc,
    NameDesc,
    DurationDesc,
    DurationAsc,
}

public sealed record FlightQuery(
    Guid? TeamId = null,
    Guid? ProjectId = null,
    Guid? AircraftId = null,
    Autopilot? Autopilot = null,
    ProcessingStatus? Status = null,
    string? Firmware = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    string? Search = null,
    FlightSort Sort = FlightSort.OccurredAtDesc,
    int Page = 1,
    int PageSize = 20);

/// <summary>A row of the flight history. Null values are unknown (for example before the log is parsed).</summary>
public sealed record FlightListItemDto(
    Guid Id,
    Guid TeamId,
    Guid ProjectId,
    string ProjectName,
    Guid? AircraftId,
    string? AircraftName,
    string Name,
    Autopilot Autopilot,
    string? FirmwareVersion,
    DateTimeOffset? StartedAt,
    DateTimeOffset OccurredAt,
    double? DurationSeconds,
    ProcessingStatus? ProcessingStatus,
    Guid? LogFileId,
    string? FileName,
    long? FileSizeBytes,
    DateTimeOffset CreatedAt,
    AnalysisSummaryDto? LatestAnalysis);

/// <summary>
/// A flight. Signal and event times are microseconds on the log's own clock; <c>LogStartTimestampUs</c> is where the
/// flight starts on that clock and <c>BootTimeUtc</c> (when known) converts it to UTC: utc = BootTimeUtc + t µs.
/// </summary>
public sealed record FlightDto(
    Guid Id,
    Guid TeamId,
    Guid ProjectId,
    string ProjectName,
    Guid? AircraftId,
    string? AircraftName,
    string Name,
    string? Notes,
    Autopilot Autopilot,
    string? FirmwareVersion,
    string? VehicleType,
    string? Hardware,
    DateTimeOffset? StartedAt,
    DateTimeOffset OccurredAt,
    double? DurationSeconds,
    DateTimeOffset? BootTimeUtc,
    long? LogStartTimestampUs,
    IReadOnlyDictionary<string, string> LogMetadata,
    Guid CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    uint Version,
    LogFileDto? LogFile,
    AnalysisSummaryDto? LatestAnalysis);

public sealed record UpdateFlightRequest(string Name, string? Notes, Guid? AircraftId, uint Version);

public sealed record LogFileDto(
    Guid Id,
    Guid FlightId,
    string FileName,
    long SizeBytes,
    string Sha256,
    LogFormat Format,
    ProcessingStatus Status,
    DateTimeOffset StatusChangedAt,
    string? FailureCode,
    string? FailureMessage,
    Guid UploadedBy,
    DateTimeOffset UploadedAt);

/// <summary>Metadata of an upload; the content itself is streamed separately.</summary>
public sealed record UploadLogRequest(string FileName, Guid? AircraftId, string? FlightName);

public sealed record UploadLogResult(FlightDto Flight, LogFileDto LogFile);

public sealed record LogFileContent(Stream Content, string FileName, long SizeBytes);
