using FlightLog.Application.Common;
using FlightLog.Application.Projects;
using FlightLog.Application.Teams;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FlightLog.Api.Controllers;

/// <summary>Teams, their members and the projects they own. Authorization is enforced in the application services.</summary>
[ApiController]
[Authorize]
[Route("api/v1/teams")]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
public sealed class TeamsController(ITeamService teams, IProjectService projects) : ControllerBase
{
    /// <summary>Teams the current user is a member of.</summary>
    [HttpGet]
    public Task<IReadOnlyList<TeamDto>> List(CancellationToken cancellationToken) => teams.ListMineAsync(cancellationToken);

    [HttpGet("{teamId:guid}")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<TeamDto> Get(Guid teamId, CancellationToken cancellationToken) => teams.GetAsync(teamId, cancellationToken);

    /// <summary>Creates a team; the caller becomes its project manager.</summary>
    [HttpPost]
    [ProducesResponseType<TeamDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<TeamDto>> Create(CreateTeamRequest request, CancellationToken cancellationToken)
    {
        var team = await teams.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { teamId = team.Id }, team);
    }

    [HttpGet("{teamId:guid}/members")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<IReadOnlyList<TeamMemberDto>> ListMembers(Guid teamId, CancellationToken cancellationToken) =>
        teams.ListMembersAsync(teamId, cancellationToken);

    /// <summary>Adds a registered user to the team (project manager only).</summary>
    [HttpPost("{teamId:guid}/members")]
    [ProducesResponseType<TeamMemberDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<TeamMemberDto>> AddMember(Guid teamId, AddTeamMemberRequest request, CancellationToken cancellationToken)
    {
        var member = await teams.AddMemberAsync(teamId, request, cancellationToken);
        return CreatedAtAction(nameof(ListMembers), new { teamId }, member);
    }

    [HttpPut("{teamId:guid}/members/{userId:guid}")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public Task<TeamMemberDto> ChangeMemberRole(Guid teamId, Guid userId, ChangeTeamMemberRoleRequest request, CancellationToken cancellationToken) =>
        teams.ChangeMemberRoleAsync(teamId, userId, request, cancellationToken);

    /// <summary>Removes a member. Members may remove themselves; removing others requires the project manager role.</summary>
    [HttpDelete("{teamId:guid}/members/{userId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> RemoveMember(Guid teamId, Guid userId, CancellationToken cancellationToken)
    {
        await teams.RemoveMemberAsync(teamId, userId, cancellationToken);
        return NoContent();
    }

    [HttpGet("{teamId:guid}/projects")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<PagedResult<ProjectDto>> ListProjects(Guid teamId, [FromQuery] ProjectQuery query, CancellationToken cancellationToken) =>
        projects.ListAsync(teamId, query, cancellationToken);

    /// <summary>Creates a project in the team (project manager only).</summary>
    [HttpPost("{teamId:guid}/projects")]
    [ProducesResponseType<ProjectDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ProjectDto>> CreateProject(Guid teamId, CreateProjectRequest request, CancellationToken cancellationToken)
    {
        var project = await projects.CreateAsync(teamId, request, cancellationToken);
        return CreatedAtAction(nameof(ProjectsController.Get), "Projects", new { projectId = project.Id }, project);
    }
}
