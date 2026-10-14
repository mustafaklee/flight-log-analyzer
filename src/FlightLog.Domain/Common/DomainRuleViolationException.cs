namespace FlightLog.Domain.Common;

/// <summary>
/// Thrown when an operation would break a business rule of the domain model.
/// <see cref="Code"/> is a stable, machine-readable identifier that clients can rely on.
/// </summary>
public sealed class DomainRuleViolationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
