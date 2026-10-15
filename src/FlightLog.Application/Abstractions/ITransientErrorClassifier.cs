namespace FlightLog.Application.Abstractions;

/// <summary>
/// Tells failures worth retrying (database or object storage unreachable, timeouts) from permanent ones (a bug,
/// a corrupt file). Implemented in Infrastructure, which knows the driver exception types.
/// </summary>
public interface ITransientErrorClassifier
{
    bool IsTransient(Exception exception);
}
