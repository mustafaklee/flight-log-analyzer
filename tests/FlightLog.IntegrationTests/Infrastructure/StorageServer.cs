using System.Net;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace FlightLog.IntegrationTests.Infrastructure;

/// <summary>
/// One SeaweedFS S3 gateway for the whole test run, the same server image as docker-compose.yml.
/// Each <see cref="ApiFactory"/> uses its own bucket.
/// </summary>
internal static class StorageServer
{
    public const string AccessKey = "flightlog-test";
    public const string SecretKey = "flightlog-test-secret";
    private const int S3Port = 8333;

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static IContainer? container;

    public static async Task<string> GetServiceUrlAsync()
    {
        await Gate.WaitAsync();
        try
        {
            if (container is null)
            {
                var instance = new ContainerBuilder("chrislusf/seaweedfs:4.48")
                    .WithCommand("mini", "-dir=/data", "-master.telemetry=false")
                    .WithEnvironment("AWS_ACCESS_KEY_ID", AccessKey)
                    .WithEnvironment("AWS_SECRET_ACCESS_KEY", SecretKey)
                    .WithPortBinding(S3Port, true)
                    .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request
                        .ForPort(S3Port)
                        .ForPath("/")
                        .ForStatusCode(HttpStatusCode.Forbidden)))
                    .Build();
                await instance.StartAsync();
                container = instance;
            }
        }
        finally
        {
            Gate.Release();
        }

        return $"http://{container.Hostname}:{container.GetMappedPublicPort(S3Port)}";
    }
}
