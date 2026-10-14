using FlightLog.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FlightLog.Infrastructure.Persistence;

public static class DatabaseInitializer
{
    /// <summary>
    /// Applies pending migrations when <c>Database:MigrateOnStartup</c> is enabled (local and test environments)
    /// and seeds identity roles. Production deployments run migrations as a separate step (see docs/deployment.md).
    /// </summary>
    public static async Task InitializeAsync(IServiceProvider services, IConfiguration configuration)
    {
        await using var scope = services.CreateAsyncScope();
        if (configuration.GetValue("Database:MigrateOnStartup", false))
        {
            var db = scope.ServiceProvider.GetRequiredService<FlightLogDbContext>();
            await db.Database.MigrateAsync();
        }

        await scope.ServiceProvider.GetRequiredService<AdministratorSeeder>().SeedAsync();
    }
}
