namespace FlightLog.Application.Abstractions;

/// <summary>Read-only lookup of user accounts, which live in the identity store.</summary>
public interface IUserDirectory
{
    Task<UserSummary?> FindByEmailAsync(string email, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, UserSummary>> GetByIdsAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken);
}

public sealed record UserSummary(Guid Id, string Email, string DisplayName);
