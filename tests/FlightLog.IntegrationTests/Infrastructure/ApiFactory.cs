using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FlightLog.IntegrationTests.Infrastructure;

/// <summary>Runs the real API (real PostgreSQL, real migrations, real authentication) in memory.</summary>
public class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string AdminEmail = "admin@flightlog.test";
    public const string AdminPassword = "Admin-Password-1";

    private string connectionString = string.Empty;
    private string storageUrl = string.Empty;

    /// <summary>Bucket used by this factory instance; isolated from other test classes.</summary>
    public string Bucket { get; } = $"test-{Guid.NewGuid():N}";

    protected virtual long MaxUploadBytes => 64L * 1024 * 1024;

    protected virtual int AuthRateLimitPerMinute => 10_000;

    protected virtual bool AllowSelfRegistration => true;

    /// <summary>Most tests drive the worker steps themselves through the database transport; see RabbitMqTests.</summary>
    protected virtual void ConfigureMessaging(IWebHostBuilder builder) => builder.UseSetting("Messaging:Transport", "Database");

    /// <summary>Starts what a derived factory needs before the host is built (for example a message broker).</summary>
    protected virtual Task PrepareAsync() => Task.CompletedTask;

    public async ValueTask InitializeAsync()
    {
        var database = PostgresServer.CreateDatabaseConnectionStringAsync();
        var storage = StorageServer.GetServiceUrlAsync();
        connectionString = await database;
        storageUrl = await storage;
        await PrepareAsync();

        // Force the host to start so migrations run before the first test.
        _ = Server;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Postgres", connectionString);
        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.UseSetting("Auth:AllowSelfRegistration", AllowSelfRegistration ? "true" : "false");
        builder.UseSetting("Auth:RateLimitPerMinute", AuthRateLimitPerMinute.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.UseSetting("Auth:BootstrapAdministrator:Email", AdminEmail);
        builder.UseSetting("Auth:BootstrapAdministrator:Password", AdminPassword);
        builder.UseSetting("Serilog:MinimumLevel:Default", "Warning");
        builder.UseSetting("Storage:ServiceUrl", storageUrl);
        builder.UseSetting("Storage:Bucket", Bucket);
        builder.UseSetting("Storage:AccessKey", StorageServer.AccessKey);
        builder.UseSetting("Storage:SecretKey", StorageServer.SecretKey);
        builder.UseSetting("Storage:CreateBucketIfMissing", "true");
        builder.UseSetting("Uploads:MaxFileSizeBytes", MaxUploadBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.UseSetting("Processing:Enabled", "false");
        ConfigureMessaging(builder);
    }
}
