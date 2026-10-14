using FlightLog.Application.Abstractions;
using FlightLog.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace FlightLog.Infrastructure.Identity;

internal sealed class UserDirectory(FlightLogDbContext db, ILookupNormalizer normalizer) : IUserDirectory
{
    public async Task<UserSummary?> FindByEmailAsync(string email, CancellationToken cancellationToken)
    {
        var normalized = normalizer.NormalizeEmail(email);
        return await db.Users
            .Where(u => u.NormalizedEmail == normalized)
            .Select(u => new UserSummary(u.Id, u.Email!, u.DisplayName))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<Guid, UserSummary>> GetByIdsAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken)
    {
        if (userIds.Count == 0)
        {
            return new Dictionary<Guid, UserSummary>();
        }

        return await db.Users
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new UserSummary(u.Id, u.Email!, u.DisplayName))
            .ToDictionaryAsync(u => u.Id, cancellationToken);
    }
}
