namespace FlightLog.Api.Auth;

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>When false, accounts are created only by administrators (invite flow comes later).</summary>
    public bool AllowSelfRegistration { get; set; }

    public int AccessTokenMinutes { get; set; } = 30;

    public int RefreshTokenDays { get; set; } = 7;

    /// <summary>Requests per minute per client IP on authentication endpoints.</summary>
    public int RateLimitPerMinute { get; set; } = 10;
}
