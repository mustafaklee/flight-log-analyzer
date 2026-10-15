namespace FlightLog.Domain.Collaboration;

/// <summary>
/// An append-only record of a change: who did what to which entity, when. Written in the same transaction as the
/// change. Entries keep only ids and the changed values, never secrets; they outlive the entities they mention
/// (a deleted flight keeps its trail) and are removed after the configured retention period.
/// </summary>
public sealed record AuditEntry
{
    public const int ActionMaxLength = 64;
    public const int EntityTypeMaxLength = 64;
    public const int SummaryMaxLength = 500;

    public required Guid Id { get; init; }

    /// <summary>Null for actions outside a team (administration of user accounts).</summary>
    public Guid? TeamId { get; init; }

    public required Guid ActorId { get; init; }

    /// <summary>Stable code, for example <c>finding.status_changed</c>.</summary>
    public required string Action { get; init; }

    public required string EntityType { get; init; }

    public required Guid EntityId { get; init; }

    /// <summary>The flight the change concerns, so that a flight's history is one indexed query.</summary>
    public Guid? FlightId { get; init; }

    /// <summary>Short human readable description (names, old and new values).</summary>
    public string? Summary { get; init; }

    /// <summary>Changed values as a flat key/value map; for example <c>from</c> and <c>to</c>.</summary>
    public IReadOnlyDictionary<string, string?> Data { get; init; } = new Dictionary<string, string?>();

    public required string CorrelationId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}
