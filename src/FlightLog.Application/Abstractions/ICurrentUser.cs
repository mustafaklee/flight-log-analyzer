namespace FlightLog.Application.Abstractions;

/// <summary>The authenticated caller of the current operation.</summary>
public interface ICurrentUser
{
    /// <summary>The user id; throws when the operation runs without an authenticated user.</summary>
    Guid UserId { get; }

    bool IsAdministrator { get; }
}
