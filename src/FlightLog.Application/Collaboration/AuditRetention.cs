using FlightLog.Application.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FlightLog.Application.Collaboration;

/// <summary>Audit trail settings (section <c>Audit</c>).</summary>
public sealed class AuditOptions
{
    public const string SectionName = "Audit";

    /// <summary>Entries older than this are deleted by the worker's maintenance; 0 keeps them forever.</summary>
    public int RetentionDays { get; set; } = 730;
}

public interface IAuditRetention
{
    /// <summary>Deletes entries past the retention period; returns how many.</summary>
    Task<int> PurgeAsync(CancellationToken cancellationToken);
}

internal sealed class AuditRetention(IApplicationDbContext db, TimeProvider clock, IOptions<AuditOptions> options) : IAuditRetention
{
    public async Task<int> PurgeAsync(CancellationToken cancellationToken)
    {
        var days = options.Value.RetentionDays;
        if (days <= 0)
        {
            return 0;
        }

        var cutoff = clock.GetUtcNow().AddDays(-days);
        return await db.AuditEntries.Where(a => a.CreatedAt < cutoff).ExecuteDeleteAsync(cancellationToken);
    }
}
