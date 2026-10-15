using FlightLog.Domain.Common;

namespace FlightLog.Domain.Flights;

/// <summary>
/// One flight of an aircraft within a project. Values that only the log parser can know
/// (start time, duration, firmware) stay null until the log has been processed.
/// </summary>
public sealed class Flight
{
    public const int NameMaxLength = 200;
    public const int NotesMaxLength = 4000;

    private Flight()
    {
        Name = null!;
    }

    private Flight(Guid id, Guid teamId, Guid projectId, DateTimeOffset now)
    {
        Id = id;
        TeamId = teamId;
        ProjectId = projectId;
        Name = null!;
        CreatedAt = now;
        UpdatedAt = now;
        OccurredAt = now;
    }

    public Guid Id { get; private set; }

    public Guid TeamId { get; private set; }

    public Guid ProjectId { get; private set; }

    public Guid? AircraftId { get; private set; }

    public string Name { get; private set; }

    public string? Notes { get; private set; }

    public Autopilot Autopilot { get; private set; }

    /// <summary>UTC start of the flight as derived from the log; null until known.</summary>
    public DateTimeOffset? StartedAt { get; private set; }

    /// <summary>Flight duration in microseconds of log time; null until known.</summary>
    public long? DurationUs { get; private set; }

    public string? FirmwareVersion { get; private set; }

    /// <summary>Vehicle class as the log reports it (for example Copter, RotaryWing, Vtol).</summary>
    public string? VehicleType { get; private set; }

    /// <summary>Flight controller board as the log reports it.</summary>
    public string? Hardware { get; private set; }

    /// <summary>UTC time of the log's timestamp zero, derived from GPS time; null without a GPS time fix.</summary>
    public DateTimeOffset? BootTimeUtc { get; private set; }

    /// <summary>Log timestamp (µs, the log's own clock) at which the flight starts; signal times are on this clock.</summary>
    public long? LogStartTimestampUs { get; private set; }

    /// <summary>Key/value facts from the log (software versions, board ids). Empty until the log is processed.</summary>
    public IReadOnlyDictionary<string, string> LogMetadata { get; private set; } = new Dictionary<string, string>();

    /// <summary>
    /// The date used for listing and filtering: <see cref="StartedAt"/> when known, otherwise the upload time.
    /// Stored so that it can be indexed.
    /// </summary>
    public DateTimeOffset OccurredAt { get; private set; }

    public Guid CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public uint Version { get; private set; }

    public static Flight Create(Guid teamId, Guid projectId, Guid? aircraftId, string name, Autopilot autopilot, Guid createdBy, DateTimeOffset now)
    {
        var flight = new Flight(Guid.CreateVersion7(now), Guard.NotEmpty(teamId, "teamId"), Guard.NotEmpty(projectId, "projectId"), now)
        {
            AircraftId = aircraftId == Guid.Empty ? null : aircraftId,
            Name = Guard.RequiredText(name, NameMaxLength, "name"),
            Autopilot = autopilot,
            CreatedBy = Guard.NotEmpty(createdBy, "createdBy"),
        };
        return flight;
    }

    public void Update(string name, string? notes, Guid? aircraftId, DateTimeOffset now)
    {
        Name = Guard.RequiredText(name, NameMaxLength, "name");
        Notes = Guard.OptionalText(notes, NotesMaxLength, "notes");
        AircraftId = aircraftId == Guid.Empty ? null : aircraftId;
        UpdatedAt = now;
    }

    /// <summary>Records what the log parser found. Unknown values stay null.</summary>
    public void RecordLogDetails(FlightLogDetails details, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(details);
        if (details.DurationUs < 0)
        {
            throw new DomainRuleViolationException("flight.negative_duration", "Flight duration cannot be negative.");
        }

        StartedAt = details.StartedAt;
        DurationUs = details.DurationUs;
        FirmwareVersion = Truncate(details.FirmwareVersion, 200);
        VehicleType = Truncate(details.VehicleType, 50);
        Hardware = Truncate(details.Hardware, 200);
        BootTimeUtc = details.BootTimeUtc;
        LogStartTimestampUs = details.LogStartTimestampUs;
        LogMetadata = new Dictionary<string, string>(details.Metadata ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        OccurredAt = details.StartedAt ?? CreatedAt;
        UpdatedAt = now;
    }

    // Values come from the log file, not from a user, so they are shortened rather than rejected.
    private static string? Truncate(string? value, int maxLength)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.Length > maxLength ? trimmed[..maxLength] : trimmed;
    }
}

/// <summary>What processing a log found out about its flight. Null means the log does not say.</summary>
public sealed record FlightLogDetails
{
    public DateTimeOffset? StartedAt { get; init; }

    public long? DurationUs { get; init; }

    public string? FirmwareVersion { get; init; }

    public string? VehicleType { get; init; }

    public string? Hardware { get; init; }

    public DateTimeOffset? BootTimeUtc { get; init; }

    public long? LogStartTimestampUs { get; init; }

    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}
