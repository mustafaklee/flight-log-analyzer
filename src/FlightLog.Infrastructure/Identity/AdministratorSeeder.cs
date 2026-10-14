using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlightLog.Infrastructure.Identity;

public sealed class BootstrapAdministratorOptions
{
    public const string SectionName = "Auth:BootstrapAdministrator";

    public string? Email { get; set; }

    /// <summary>Supplied through an environment variable or secret store only; never committed.</summary>
    public string? Password { get; set; }

    public string DisplayName { get; set; } = "Administrator";
}

/// <summary>
/// Ensures the Administrator role exists and, when configured, creates the first administrator account.
/// Existing accounts are never modified, so the seeder is safe to run on every start.
/// </summary>
public sealed partial class AdministratorSeeder(
    RoleManager<IdentityRole<Guid>> roles,
    UserManager<ApplicationUser> users,
    IOptions<BootstrapAdministratorOptions> options,
    TimeProvider clock,
    ILogger<AdministratorSeeder> logger)
{
    public async Task SeedAsync()
    {
        if (!await roles.RoleExistsAsync(SystemRoles.Administrator))
        {
            EnsureSucceeded(await roles.CreateAsync(new IdentityRole<Guid>(SystemRoles.Administrator)));
        }

        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.Email) || string.IsNullOrWhiteSpace(settings.Password))
        {
            return;
        }

        if (await users.FindByEmailAsync(settings.Email) is not null)
        {
            return;
        }

        var admin = new ApplicationUser
        {
            UserName = settings.Email,
            Email = settings.Email,
            DisplayName = settings.DisplayName,
            CreatedAt = clock.GetUtcNow(),
        };
        var created = await users.CreateAsync(admin, settings.Password);
        if (!created.Succeeded)
        {
            throw new InvalidOperationException(
                $"The bootstrap administrator ({BootstrapAdministratorOptions.SectionName}) could not be created: "
                + string.Join("; ", created.Errors.Select(e => e.Description)));
        }

        EnsureSucceeded(await users.AddToRoleAsync(admin, SystemRoles.Administrator));
        LogAdministratorCreated(logger, admin.Id);
    }

    private static void EnsureSucceeded(IdentityResult result)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                "Identity seeding failed: " + string.Join("; ", result.Errors.Select(e => e.Code)));
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Bootstrap administrator account {UserId} created")]
    private static partial void LogAdministratorCreated(ILogger logger, Guid userId);
}
