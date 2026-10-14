using FlightLog.Application.Common;
using FlightLog.Infrastructure.Identity;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FlightLog.Api.Controllers;

public sealed record CreateUserRequest(string Email, string DisplayName, string Password, bool IsAdministrator = false);

public sealed record AdminUserDto(Guid Id, string Email, string DisplayName, bool IsAdministrator, bool IsDisabled, DateTimeOffset CreatedAt);

internal sealed class CreateUserRequestValidator : AbstractValidator<CreateUserRequest>
{
    public CreateUserRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(ApplicationUser.DisplayNameMaxLength);
        RuleFor(x => x.Password).NotEmpty().MaximumLength(128);
    }
}

/// <summary>
/// Account administration for system administrators: the way to create accounts when self registration is off.
/// Identity accounts belong to the API/infrastructure boundary, so this controller uses ASP.NET Core Identity directly.
/// </summary>
[ApiController]
[Authorize(Roles = SystemRoles.Administrator)]
[Route("api/v1/admin/users")]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
public sealed class AdminUsersController(UserManager<ApplicationUser> users, TimeProvider clock) : ControllerBase
{
    [HttpGet]
    public async Task<PagedResult<AdminUserDto>> List([FromQuery] string? search, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken cancellationToken = default)
    {
        var paging = new PageRequest(page, pageSize);
        var query = users.Users.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToUpperInvariant();
#pragma warning disable CA1304, CA1311, CA1862 // Translated to SQL upper(); culture overloads are not translatable.
            query = query.Where(u => u.NormalizedEmail!.Contains(term) || u.DisplayName.ToUpper().Contains(term));
#pragma warning restore CA1304, CA1311, CA1862
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderBy(u => u.Email).Skip(paging.Skip).Take(paging.SafePageSize).ToListAsync(cancellationToken);
        var admins = (await users.GetUsersInRoleAsync(SystemRoles.Administrator)).Select(u => u.Id).ToHashSet();
        var now = clock.GetUtcNow();
        return new PagedResult<AdminUserDto>(
            items.Select(u => ToDto(u, admins.Contains(u.Id), now)).ToList(), paging.SafePage, paging.SafePageSize, total);
    }

    [HttpPost]
    [ProducesResponseType<AdminUserDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<AdminUserDto>> Create(CreateUserRequest request, [FromServices] IValidator<CreateUserRequest> validator, CancellationToken cancellationToken)
    {
        var validation = await validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            throw new ValidationException(validation.Errors);
        }

        var user = new ApplicationUser
        {
            UserName = request.Email,
            Email = request.Email,
            DisplayName = request.DisplayName.Trim(),
            CreatedAt = clock.GetUtcNow(),
        };
        var result = await users.CreateAsync(user, request.Password);
        if (!result.Succeeded)
        {
            throw new ValidationException(result.Errors.Select(e => new ValidationFailure(
                e.Code.Contains("Password", StringComparison.Ordinal) ? nameof(request.Password) : nameof(request.Email), e.Description)));
        }

        if (request.IsAdministrator)
        {
            await users.AddToRoleAsync(user, SystemRoles.Administrator);
        }

        return Created($"/api/v1/admin/users/{user.Id}", ToDto(user, request.IsAdministrator, clock.GetUtcNow()));
    }

    /// <summary>
    /// Blocks sign-in and token refresh for the account. Access tokens already issued stay valid until they expire
    /// (<c>Auth:AccessTokenMinutes</c>).
    /// </summary>
    [HttpPost("{userId:guid}/disable")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<AdminUserDto> Disable(Guid userId)
    {
        if (User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value == userId.ToString())
        {
            throw new Domain.Common.DomainRuleViolationException("user.cannot_disable_self", "Administrators cannot disable their own account.");
        }

        var user = await FindAsync(userId);
        await users.SetLockoutEnabledAsync(user, true);
        await users.SetLockoutEndDateAsync(user, DateTimeOffset.MaxValue);
        await users.UpdateSecurityStampAsync(user); // invalidates refresh tokens
        return ToDto(user, await users.IsInRoleAsync(user, SystemRoles.Administrator), clock.GetUtcNow());
    }

    [HttpPost("{userId:guid}/enable")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<AdminUserDto> Enable(Guid userId)
    {
        var user = await FindAsync(userId);
        await users.SetLockoutEndDateAsync(user, null);
        await users.ResetAccessFailedCountAsync(user);
        return ToDto(user, await users.IsInRoleAsync(user, SystemRoles.Administrator), clock.GetUtcNow());
    }

    private async Task<ApplicationUser> FindAsync(Guid userId) =>
        await users.FindByIdAsync(userId.ToString()) ?? throw new NotFoundException("User", userId);

    private static AdminUserDto ToDto(ApplicationUser u, bool isAdmin, DateTimeOffset now) =>
        new(u.Id, u.Email!, u.DisplayName, isAdmin, u.LockoutEnd > now.AddYears(50), u.CreatedAt);
}
