using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FlightLog.Infrastructure.Storage;

internal sealed class StorageHealthCheck(S3FileStorage storage) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await storage.IsBucketReachableAsync(timeout.Token);
            return HealthCheckResult.Healthy();
        }
#pragma warning disable CA1031 // A health check reports every failure instead of throwing.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return HealthCheckResult.Unhealthy("Object storage is not reachable.", ex);
        }
    }
}
