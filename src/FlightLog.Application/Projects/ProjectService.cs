using FlightLog.Application.Abstractions;
using FlightLog.Application.Authorization;
using FlightLog.Application.Common;
using FlightLog.Domain.Projects;
using FlightLog.Domain.Teams;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace FlightLog.Application.Projects;

public interface IProjectService
{
    Task<PagedResult<ProjectDto>> ListAsync(Guid teamId, ProjectQuery query, CancellationToken cancellationToken);

    Task<ProjectDto> GetAsync(Guid projectId, CancellationToken cancellationToken);

    Task<ProjectDto> CreateAsync(Guid teamId, CreateProjectRequest request, CancellationToken cancellationToken);

    Task<ProjectDto> UpdateAsync(Guid projectId, UpdateProjectRequest request, CancellationToken cancellationToken);

    Task DeleteAsync(Guid projectId, CancellationToken cancellationToken);
}

internal sealed class ProjectService(
    IApplicationDbContext db,
    ITeamAccessService access,
    TimeProvider clock,
    IValidator<CreateProjectRequest> createValidator,
    IValidator<UpdateProjectRequest> updateValidator) : IProjectService
{
    public async Task<PagedResult<ProjectDto>> ListAsync(Guid teamId, ProjectQuery query, CancellationToken cancellationToken)
    {
        await access.EnsureRoleAsync(teamId, TeamRole.Viewer, cancellationToken);

        var paging = new PageRequest(query.Page, query.PageSize);
        var projects = db.Projects.AsNoTracking().Where(p => p.TeamId == teamId);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            // EF Core translates Contains to a parameterized strpos/LIKE with proper escaping.
            var term = query.Search.Trim().ToLowerInvariant();
#pragma warning disable CA1304, CA1311, CA1862 // Translated to SQL lower(); culture overloads are not translatable.
            projects = projects.Where(p => p.Name.ToLower().Contains(term));
#pragma warning restore CA1304, CA1311, CA1862
        }

        var total = await projects.CountAsync(cancellationToken);
        var items = await projects
            .OrderBy(p => p.Name)
            .ThenBy(p => p.Id)
            .Skip(paging.Skip)
            .Take(paging.SafePageSize)
            .Select(p => new ProjectDto(p.Id, p.TeamId, p.Name, p.Description, p.CreatedAt, p.UpdatedAt, p.Version))
            .ToListAsync(cancellationToken);

        return new PagedResult<ProjectDto>(items, paging.SafePage, paging.SafePageSize, total);
    }

    public async Task<ProjectDto> GetAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await LoadAuthorizedAsync(projectId, TeamRole.Viewer, cancellationToken);
        return ToDto(project);
    }

    public async Task<ProjectDto> CreateAsync(Guid teamId, CreateProjectRequest request, CancellationToken cancellationToken)
    {
        await createValidator.EnsureValidAsync(request, cancellationToken);
        await access.EnsureRoleAsync(teamId, TeamRole.ProjectManager, cancellationToken);

        var project = Project.Create(teamId, request.Name, request.Description, clock.GetUtcNow());
        await EnsureNameIsFreeAsync(teamId, project.Name, exceptProjectId: null, cancellationToken);

        db.Projects.Add(project);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(project);
    }

    public async Task<ProjectDto> UpdateAsync(Guid projectId, UpdateProjectRequest request, CancellationToken cancellationToken)
    {
        await updateValidator.EnsureValidAsync(request, cancellationToken);
        var project = await LoadAuthorizedAsync(projectId, TeamRole.ProjectManager, cancellationToken);
        if (project.Version != request.Version)
        {
            throw StaleVersion();
        }

        project.Update(request.Name, request.Description, clock.GetUtcNow());
        await EnsureNameIsFreeAsync(project.TeamId, project.Name, project.Id, cancellationToken);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw StaleVersion();
        }

        return ToDto(project);
    }

    public async Task DeleteAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await LoadAuthorizedAsync(projectId, TeamRole.ProjectManager, cancellationToken);
        if (await db.Flights.AnyAsync(f => f.ProjectId == projectId, cancellationToken))
        {
            throw new ConflictException("project.has_flights", "The project still has flights. Delete or move them first.");
        }

        db.Projects.Remove(project);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<Project> LoadAuthorizedAsync(Guid projectId, TeamRole required, CancellationToken cancellationToken)
    {
        var project = await db.Projects.SingleOrDefaultAsync(p => p.Id == projectId, cancellationToken)
            ?? throw new NotFoundException("Project", projectId);

        // A user outside the owning team gets the same 404 as for a missing project.
        await access.EnsureResourceRoleAsync(project.TeamId, required, "Project", projectId, cancellationToken);
        return project;
    }

    private async Task EnsureNameIsFreeAsync(Guid teamId, string name, Guid? exceptProjectId, CancellationToken cancellationToken)
    {
        var taken = await db.Projects.AnyAsync(
            p => p.TeamId == teamId && p.Name == name && p.Id != exceptProjectId,
            cancellationToken);
        if (taken)
        {
            throw new ConflictException("project.name_taken", "A project with this name already exists in the team.");
        }
    }

    private static ConflictException StaleVersion() =>
        new("project.version_mismatch", "The project was modified by someone else. Reload it and try again.");

    private static ProjectDto ToDto(Project p) =>
        new(p.Id, p.TeamId, p.Name, p.Description, p.CreatedAt, p.UpdatedAt, p.Version);
}
