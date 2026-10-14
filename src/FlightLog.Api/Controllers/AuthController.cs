using FlightLog.Api.Auth;
using FlightLog.Application.Abstractions;
using FlightLog.Application.Common;
using FlightLog.Infrastructure.Identity;
using FlightLog.Infrastructure.Persistence;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FlightLog.Api.Controllers;

/// <summary>Account registration and token issuance (ASP.NET Core Identity bearer tokens, see ADR-0003).</summary>
[ApiController]
[Route("api/v1/auth")]
[EnableRateLimiting(FlightLog.Api.Infrastructure.RateLimitPolicies.Auth)]
public sealed class AuthController(
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager,
    IOptionsMonitor<BearerTokenOptions> bearerOptions,
    IOptions<AuthOptions> authOptions,
    TimeProvider clock) : ControllerBase
{
    /// <summary>Creates an account. Disabled unless <c>Auth:AllowSelfRegistration</c> is true.</summary>
    [HttpPost("register")]
    [AllowAnonymous]
    [ProducesResponseType<UserProfileDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Register(RegisterRequest request, [FromServices] IValidator<RegisterRequest> validator, CancellationToken cancellationToken)
    {
        if (!authOptions.Value.AllowSelfRegistration)
        {
            throw new ForbiddenException("Self registration is disabled. Ask an administrator for an account.");
        }

        await EnsureValidAsync(validator, request, cancellationToken);

        var user = new ApplicationUser
        {
            UserName = request.Email,
            Email = request.Email,
            DisplayName = request.DisplayName.Trim(),
            CreatedAt = clock.GetUtcNow(),
        };
        var result = await userManager.CreateAsync(user, request.Password);
        if (!result.Succeeded)
        {
            // Identity reports duplicate e-mails as errors; surface them as validation failures without
            // revealing anything beyond what the registration form already implies.
            throw new ValidationException(result.Errors.Select(e => new ValidationFailure(FieldFor(e.Code), e.Description)));
        }

        return Created("/api/v1/auth/me", new UserProfileDto(user.Id, user.Email, user.DisplayName, [], []));
    }

    /// <summary>Exchanges e-mail and password for an access token and a refresh token.</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [ProducesResponseType<AccessTokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login(LoginRequest request, [FromServices] IValidator<LoginRequest> validator, CancellationToken cancellationToken)
    {
        await EnsureValidAsync(validator, request, cancellationToken);

        signInManager.AuthenticationScheme = IdentityConstants.BearerScheme;
        var result = await signInManager.PasswordSignInAsync(request.Email, request.Password, isPersistent: false, lockoutOnFailure: true);
        if (!result.Succeeded)
        {
            // Same answer for unknown user, wrong password and lockout, so accounts cannot be enumerated.
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Login failed", detail: "Invalid credentials or the account is temporarily locked.");
        }

        // The bearer token handler has already written the AccessTokenResponse body.
        return new EmptyResult();
    }

    /// <summary>Issues a new token pair from a valid refresh token.</summary>
    [HttpPost("refresh")]
    [AllowAnonymous]
    [ProducesResponseType<AccessTokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Refresh(RefreshRequest request)
    {
        var protector = bearerOptions.Get(IdentityConstants.BearerScheme).RefreshTokenProtector;
        var ticket = string.IsNullOrEmpty(request.RefreshToken) ? null : protector.Unprotect(request.RefreshToken);

        if (ticket?.Properties.ExpiresUtc is not { } expiresUtc
            || clock.GetUtcNow() >= expiresUtc
            || await signInManager.ValidateSecurityStampAsync(ticket.Principal) is not { } user)
        {
            return Unauthorized();
        }

        var principal = await signInManager.CreateUserPrincipalAsync(user);
        return SignIn(principal, IdentityConstants.BearerScheme);
    }

    /// <summary>Profile of the authenticated user, including system roles and team memberships.</summary>
    [HttpGet("me")]
    [Authorize]
    [ProducesResponseType<UserProfileDto>(StatusCodes.Status200OK)]
    public async Task<UserProfileDto> Me([FromServices] ICurrentUser currentUser, [FromServices] FlightLogDbContext db, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(currentUser.UserId.ToString())
            ?? throw new NotFoundException("User", currentUser.UserId);
        var roles = await userManager.GetRolesAsync(user);
        var teams = await db.TeamMembers
            .Where(m => m.UserId == user.Id)
            .Join(db.Teams, m => m.TeamId, t => t.Id, (m, t) => new { t.Id, t.Name, m.Role })
            .OrderBy(x => x.Name)
            .Select(x => new MembershipDto(x.Id, x.Name, x.Role))
            .ToListAsync(cancellationToken);

        return new UserProfileDto(user.Id, user.Email!, user.DisplayName, roles.ToList(), teams);
    }

    private static async Task EnsureValidAsync<T>(IValidator<T> validator, T request, CancellationToken cancellationToken)
    {
        var result = await validator.ValidateAsync(request, cancellationToken);
        if (!result.IsValid)
        {
            throw new ValidationException(result.Errors);
        }
    }

    private static string FieldFor(string identityErrorCode) =>
        identityErrorCode.Contains("Password", StringComparison.Ordinal) ? nameof(RegisterRequest.Password)
        : identityErrorCode.Contains("Email", StringComparison.Ordinal) || identityErrorCode.Contains("UserName", StringComparison.Ordinal) ? nameof(RegisterRequest.Email)
        : string.Empty;
}
