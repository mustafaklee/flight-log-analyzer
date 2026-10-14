using FlightLog.Domain.Common;
using FlightLog.Domain.Fleet;

namespace FlightLog.Application.Fleet;

public sealed record SaveAircraftRequest(
    string Name,
    string? Identifier,
    AircraftType Type,
    Autopilot Autopilot,
    string? FirmwareName,
    string? FirmwareVersion,
    string? FlightController,
    string? Hardware,
    string? Description,
    IReadOnlyCollection<string>? Tags,
    uint? Version = null)
{
    internal AircraftDetails ToDetails() =>
        new(Name, Identifier, Type, Autopilot, FirmwareName, FirmwareVersion, FlightController, Hardware, Description, Tags);
}

public sealed record AircraftDto(
    Guid Id,
    Guid TeamId,
    string Name,
    string? Identifier,
    AircraftType Type,
    Autopilot Autopilot,
    string? FirmwareName,
    string? FirmwareVersion,
    string? FlightController,
    string? Hardware,
    string? Description,
    IReadOnlyList<string> Tags,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    uint Version);

public sealed record AircraftQuery(string? Search = null, Autopilot? Autopilot = null, int Page = 1, int PageSize = 20);
