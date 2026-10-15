using FlightLog.Domain.Flights;

namespace FlightLog.Application.Flights;

internal static class FlightMapping
{
    public static double? ToSeconds(long? durationUs) => durationUs / 1_000_000d;

    public static LogFileDto ToDto(FlightLogFile f) =>
        new(f.Id, f.FlightId, f.OriginalFileName, f.SizeBytes, f.Sha256, f.Format, f.Status, f.StatusChangedAt,
            f.FailureCode, f.FailureMessage, f.UploadedBy, f.UploadedAt);

    public static FlightDto ToDto(Flight f, string projectName, string? aircraftName, FlightLogFile? file) =>
        new(f.Id, f.TeamId, f.ProjectId, projectName, f.AircraftId, aircraftName, f.Name, f.Notes, f.Autopilot,
            f.FirmwareVersion, f.VehicleType, f.Hardware, f.StartedAt, f.OccurredAt, ToSeconds(f.DurationUs), f.BootTimeUtc,
            f.LogStartTimestampUs, f.LogMetadata, f.CreatedBy, f.CreatedAt, f.UpdatedAt,
            f.Version, file is null ? null : ToDto(file));
}
