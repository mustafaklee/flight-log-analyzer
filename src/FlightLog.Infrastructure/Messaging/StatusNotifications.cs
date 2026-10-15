using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlightLog.Domain.Analysis;
using FlightLog.Domain.Flights;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace FlightLog.Infrastructure.Messaging;

/// <summary>A status change of a log file, analysis run or AI analysis, as sent on <see cref="StatusNotifications.Channel"/> (ADR-0011).</summary>
public sealed record StatusNotification(
    string Kind,
    Guid TeamId,
    Guid FlightId,
    Guid Id,
    string Status,
    string? FailureCode)
{
    public const string LogFile = "logFile";
    public const string AnalysisRun = "analysisRun";
    public const string AiAnalysis = "aiAnalysis";
}

public static class StatusNotifications
{
    /// <summary>PostgreSQL notification channel.</summary>
    public const string Channel = "flightlog_status";

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>
/// Sends a PostgreSQL notification for every status change of a log file or analysis run (ADR-0011). Changes are
/// collected before SaveChanges and sent after it, on the same connection: inside an explicit transaction
/// PostgreSQL delivers them only on commit; otherwise SaveChanges has already committed.
/// </summary>
internal sealed class StatusNotificationInterceptor : SaveChangesInterceptor
{
    private readonly ConditionalWeakTable<DbContext, List<StatusNotification>> _pending = [];

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Collect(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Collect(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        if (Take(eventData.Context) is { } pending)
        {
            foreach (var notification in pending)
            {
                eventData.Context!.Database.ExecuteSql(Sql(notification));
            }
        }

        return base.SavedChanges(eventData, result);
    }

    public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        if (Take(eventData.Context) is { } pending)
        {
            foreach (var notification in pending)
            {
                // Not cancellable: the change is saved, so the notification must still go out.
                await eventData.Context!.Database.ExecuteSqlAsync(Sql(notification), CancellationToken.None);
            }
        }

        return await base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        Take(eventData.Context);
        base.SaveChangesFailed(eventData);
    }

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Take(eventData.Context);
        return base.SaveChangesFailedAsync(eventData, cancellationToken);
    }

    private static FormattableString Sql(StatusNotification notification)
    {
        var payload = JsonSerializer.Serialize(notification, StatusNotifications.JsonOptions);
        return $"SELECT pg_notify({StatusNotifications.Channel}, {payload})";
    }

    private void Collect(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var notifications = new List<StatusNotification>();
        foreach (var entry in context.ChangeTracker.Entries<FlightLogFile>())
        {
            if (entry.State == EntityState.Added || (entry.State == EntityState.Modified && entry.Property(f => f.Status).IsModified))
            {
                var f = entry.Entity;
                notifications.Add(new StatusNotification(StatusNotification.LogFile, f.TeamId, f.FlightId, f.Id, f.Status.ToString(), f.FailureCode));
            }
        }

        foreach (var entry in context.ChangeTracker.Entries<AnalysisRun>())
        {
            if (entry.State == EntityState.Added || (entry.State == EntityState.Modified && entry.Property(r => r.Status).IsModified))
            {
                var r = entry.Entity;
                notifications.Add(new StatusNotification(StatusNotification.AnalysisRun, r.TeamId, r.FlightId, r.Id, r.Status.ToString(), r.FailureCode));
            }
        }

        foreach (var entry in context.ChangeTracker.Entries<AiAnalysis>())
        {
            if (entry.State == EntityState.Added || (entry.State == EntityState.Modified && entry.Property(a => a.Status).IsModified))
            {
                var a = entry.Entity;
                notifications.Add(new StatusNotification(StatusNotification.AiAnalysis, a.TeamId, a.FlightId, a.Id, a.Status.ToString(), a.FailureCode));
            }
        }

        if (notifications.Count > 0)
        {
            _pending.AddOrUpdate(context, notifications);
        }
    }

    private List<StatusNotification>? Take(DbContext? context) =>
        context is not null && _pending.TryGetValue(context, out var pending) && _pending.Remove(context) ? pending : null;
}
