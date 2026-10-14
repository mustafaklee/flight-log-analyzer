using Microsoft.AspNetCore.Identity;

namespace FlightLog.Infrastructure.Identity;

public sealed class ApplicationUser : IdentityUser<Guid>
{
    public const int DisplayNameMaxLength = 100;

    public string DisplayName { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
}
