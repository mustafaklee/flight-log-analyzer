using FlightLog.Domain.Common;

namespace FlightLog.Domain.Projects;

/// <summary>A project groups flights of a team (e.g. a test campaign or a product line).</summary>
public sealed class Project
{
    public const int NameMaxLength = 150;
    public const int DescriptionMaxLength = 2000;

    private Project()
    {
        Name = null!;
    }

    private Project(Guid id, Guid teamId, string name, string? description, DateTimeOffset now)
    {
        Id = id;
        TeamId = teamId;
        Name = name;
        Description = description;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public Guid Id { get; private set; }

    public Guid TeamId { get; private set; }

    public string Name { get; private set; }

    public string? Description { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Optimistic concurrency token; mapped to PostgreSQL's <c>xmin</c> system column.</summary>
    public uint Version { get; private set; }

    public static Project Create(Guid teamId, string name, string? description, DateTimeOffset now) =>
        new(
            Guid.CreateVersion7(now),
            Guard.NotEmpty(teamId, "teamId"),
            Guard.RequiredText(name, NameMaxLength, "name"),
            Guard.OptionalText(description, DescriptionMaxLength, "description"),
            now);

    public void Update(string name, string? description, DateTimeOffset now)
    {
        Name = Guard.RequiredText(name, NameMaxLength, "name");
        Description = Guard.OptionalText(description, DescriptionMaxLength, "description");
        UpdatedAt = now;
    }
}
