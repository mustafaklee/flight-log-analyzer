using FlightLog.Application.Projects;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FlightLog.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/projects")]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
public sealed class ProjectsController(IProjectService projects) : ControllerBase
{
    [HttpGet("{projectId:guid}")]
    public Task<ProjectDto> Get(Guid projectId, CancellationToken cancellationToken) =>
        projects.GetAsync(projectId, cancellationToken);

    /// <summary>Updates a project. The request must carry the version last read; a stale version returns 409.</summary>
    [HttpPut("{projectId:guid}")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<ProjectDto> Update(Guid projectId, UpdateProjectRequest request, CancellationToken cancellationToken) =>
        projects.UpdateAsync(projectId, request, cancellationToken);

    [HttpDelete("{projectId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Delete(Guid projectId, CancellationToken cancellationToken)
    {
        await projects.DeleteAsync(projectId, cancellationToken);
        return NoContent();
    }
}
