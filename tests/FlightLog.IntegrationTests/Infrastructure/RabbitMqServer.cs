using Testcontainers.RabbitMq;

namespace FlightLog.IntegrationTests.Infrastructure;

/// <summary>One RabbitMQ container for the test run; each factory gets its own virtual host.</summary>
internal static class RabbitMqServer
{
    public const string UserName = "flightlog";
    public const string Password = "Test-RabbitMq-1";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static RabbitMqContainer? container;

    public static async Task<(string Host, int Port)> GetAsync()
    {
        await Gate.WaitAsync();
        try
        {
            if (container is null)
            {
                var instance = new RabbitMqBuilder("rabbitmq:4.1-management-alpine").WithUsername(UserName).WithPassword(Password).Build();
                await instance.StartAsync();
                container = instance;
            }

            return (container.Hostname, container.GetMappedPublicPort(5672));
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Creates a virtual host with full rights for the test user.</summary>
    public static async Task CreateVirtualHostAsync(string name)
    {
        await GetAsync();
        await Exec("rabbitmqctl", "add_vhost", name);
        await Exec("rabbitmqctl", "set_permissions", "-p", name, UserName, ".*", ".*", ".*");
    }

    private static async Task Exec(params string[] command)
    {
        var result = await container!.ExecAsync(command);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"{string.Join(' ', command)} failed: {result.Stderr}");
        }
    }
}
