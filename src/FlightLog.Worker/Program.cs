using FlightLog.Application;
using FlightLog.Infrastructure;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(formatProvider: System.Globalization.CultureInfo.InvariantCulture)
    .CreateLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);
    builder.Services.AddSerilog((services, configuration) => configuration
        .ReadFrom.Configuration(builder.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext());

    // The worker always processes; the API only does when Processing:Enabled is set (local development).
    builder.Configuration["Processing:Enabled"] = "true";
    builder.Services.AddApplication();
    builder.Services.AddInfrastructure(builder.Configuration);

    // Graceful shutdown puts the current file back into the queue; give it time to do so.
    builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(30));

    await builder.Build().RunAsync();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "FlightLog worker terminated unexpectedly");
    throw;
}
finally
{
    await Log.CloseAndFlushAsync();
}
