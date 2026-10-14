using FlightLog.Domain.Common;

namespace FlightLog.Domain.Fleet;

/// <summary>
/// A vehicle of a team's fleet. Optional technical details stay null when unknown; they are never guessed.
/// </summary>
public sealed class Aircraft
{
    public const int NameMaxLength = 100;
    public const int IdentifierMaxLength = 100;
    public const int TextMaxLength = 200;
    public const int DescriptionMaxLength = 2000;

    private List<string> _tags = [];

    private Aircraft()
    {
        Name = null!;
    }

    private Aircraft(Guid id, Guid teamId, DateTimeOffset now)
    {
        Id = id;
        TeamId = teamId;
        Name = null!;
        CreatedAt = now;
    }

    public Guid Id { get; private set; }

    public Guid TeamId { get; private set; }

    public string Name { get; private set; }

    /// <summary>Team-unique identifier such as a serial number or MAVLink system id; optional.</summary>
    public string? Identifier { get; private set; }

    public AircraftType Type { get; private set; }

    public Autopilot Autopilot { get; private set; }

    public string? FirmwareName { get; private set; }

    public string? FirmwareVersion { get; private set; }

    public string? FlightController { get; private set; }

    public string? Hardware { get; private set; }

    public string? Description { get; private set; }

    public IReadOnlyList<string> Tags => _tags;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public uint Version { get; private set; }

    public static Aircraft Create(Guid teamId, AircraftDetails details, DateTimeOffset now)
    {
        var aircraft = new Aircraft(Guid.CreateVersion7(now), Guard.NotEmpty(teamId, "teamId"), now);
        aircraft.Update(details, now);
        return aircraft;
    }

    public void Update(AircraftDetails details, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(details);
        if (!Enum.IsDefined(details.Type))
        {
            throw new DomainRuleViolationException("aircraft.invalid_type", "Unknown aircraft type.");
        }

        if (!Enum.IsDefined(details.Autopilot))
        {
            throw new DomainRuleViolationException("aircraft.invalid_autopilot", "Unknown autopilot.");
        }

        Name = Guard.RequiredText(details.Name, NameMaxLength, "name");
        Identifier = Guard.OptionalText(details.Identifier, IdentifierMaxLength, "identifier");
        Type = details.Type;
        Autopilot = details.Autopilot;
        FirmwareName = Guard.OptionalText(details.FirmwareName, TextMaxLength, "firmwareName");
        FirmwareVersion = Guard.OptionalText(details.FirmwareVersion, TextMaxLength, "firmwareVersion");
        FlightController = Guard.OptionalText(details.FlightController, TextMaxLength, "flightController");
        Hardware = Guard.OptionalText(details.Hardware, TextMaxLength, "hardware");
        Description = Guard.OptionalText(details.Description, DescriptionMaxLength, "description");
        _tags = Common.Tags.Normalize(details.Tags);
        UpdatedAt = now;
    }
}

/// <summary>Editable aircraft attributes.</summary>
public sealed record AircraftDetails(
    string Name,
    string? Identifier,
    AircraftType Type,
    Autopilot Autopilot,
    string? FirmwareName,
    string? FirmwareVersion,
    string? FlightController,
    string? Hardware,
    string? Description,
    IReadOnlyCollection<string>? Tags);
