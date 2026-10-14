namespace FlightLog.Application.Projects;

public sealed record CreateProjectRequest(string Name, string? Description);

/// <summary>Update request; <see cref="Version"/> must be the version the client last read (optimistic concurrency).</summary>
public sealed record UpdateProjectRequest(string Name, string? Description, uint Version);

public sealed record ProjectDto(
    Guid Id,
    Guid TeamId,
    string Name,
    string? Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    uint Version);

public sealed record ProjectQuery(string? Search = null, int Page = 1, int PageSize = 20);
