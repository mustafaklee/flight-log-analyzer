using FlightLog.Application.Abstractions;

namespace FlightLog.Application.Collaboration;

internal static class UserNames
{
    public static async Task<Func<Guid, UserRefDto>> ResolveAsync(IUserDirectory users, IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        var distinct = ids.Where(id => id != Guid.Empty).Distinct().ToList();
        var found = distinct.Count == 0 ? new Dictionary<Guid, UserSummary>() : await users.GetByIdsAsync(distinct, cancellationToken);

        // Accounts are never hard-deleted; an unknown id still gets a stable placeholder rather than an error.
        return id => found.TryGetValue(id, out var user) ? new UserRefDto(id, user.DisplayName) : new UserRefDto(id, "?");
    }
}
