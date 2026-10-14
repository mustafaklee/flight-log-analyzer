using Testcontainers.PostgreSql;

namespace FlightLog.IntegrationTests.Infrastructure;

/// <summary>
/// One PostgreSQL container for the whole test run (Testcontainers removes it afterwards).
/// Each <see cref="ApiFactory"/> gets its own database on it, so test classes stay isolated.
/// </summary>
internal static class PostgresServer
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static PostgreSqlContainer? container;

    public static async Task<string> CreateDatabaseConnectionStringAsync()
    {
        await Gate.WaitAsync();
        try
        {
            if (container is null)
            {
                var instance = new PostgreSqlBuilder("postgres:17-alpine").Build();
                await instance.StartAsync();
                container = instance;
            }
        }
        finally
        {
            Gate.Release();
        }

        var builder = new Npgsql.NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            Database = $"flightlog_test_{Guid.NewGuid():N}",
        };
        return builder.ConnectionString;
    }
}
