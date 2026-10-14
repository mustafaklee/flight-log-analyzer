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
    public void RecordLogMetadata(DateTimeOffset? startedAt, long? durationUs, string? firmwareVersion, DateTimeOffset now)
    {
        if (durationUs < 0)
        {
            throw new DomainRuleViolationException("flight.negative_duration", "Flight duration cannot be negative.");
        }

        StartedAt = startedAt;
        DurationUs = durationUs;
        FirmwareVersion = Guard.OptionalText(firmwareVersion, 200, "firmwareVersion");
        OccurredAt = startedAt ?? CreatedAt;
        UpdatedAt = now;
    }
}
