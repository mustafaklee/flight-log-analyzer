using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using FlightLog.Api.Auth;
using FlightLog.Api.Infrastructure;
using FlightLog.Api.Realtime;
using FlightLog.Application;
using FlightLog.Application.Abstractions;
using FlightLog.Infrastructure;
using FlightLog.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Identity;
using Serilog;

// Startup logger for failures before the host is built. It is replaced by the configured logger below.
// (A reloadable bootstrap logger is avoided because it can only be frozen once per process, which breaks
// hosting several API instances in one test run.)
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(formatProvider: System.Globalization.CultureInfo.InvariantCulture)
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Services.AddSerilog((services, configuration) => configuration
        .ReadFrom.Configuration(builder.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext());

    builder.Services.AddApplication();
    builder.Services.AddInfrastructure(builder.Configuration);
    builder.Services.AddValidatorsFromAssemblyContaining<Program>(includeInternalTypes: true);

    var authSection = builder.Configuration.GetSection(AuthOptions.SectionName);
    builder.Services.Configure<AuthOptions>(authSection);
    var authOptions = authSection.Get<AuthOptions>() ?? new AuthOptions();

    builder.Services.AddAuthentication(IdentityConstants.BearerScheme)
        .AddBearerToken(IdentityConstants.BearerScheme, options =>
        {
            options.BearerTokenExpiration = TimeSpan.FromMinutes(authOptions.AccessTokenMinutes);
            options.RefreshTokenExpiration = TimeSpan.FromDays(authOptions.RefreshTokenDays);

            // Browsers cannot set headers on WebSocket and server-sent event requests: SignalR sends the token in
            // the query string, which is accepted on the hub path only.
            options.Events.OnMessageReceived = context =>
            {
                if (context.HttpContext.Request.Path.StartsWithSegments(ProcessingHub.Path)
                    && context.Request.Query["access_token"] is { Count: 1 } token)
                {
                    context.Token = token;
                }

                return Task.CompletedTask;
            };
        });
    builder.Services.AddAuthorization();

    builder.Services.AddHttpContextAccessor();
    builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();

    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.AddPolicy(RateLimitPolicies.Auth, context =>
            RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = authOptions.RateLimitPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));
    });

    builder.Services.AddProblemDetails(options =>
        options.CustomizeProblemDetails = context =>
            context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier);
    builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

    builder.Services.AddControllers(options => options.Conventions.Add(new SuccessResponseConvention()))
        .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

    // The OpenAPI generator reads the minimal-API JSON options: enums are documented as the strings MVC sends.
    // Numbers are documented as numbers (the web defaults would also allow numeric strings).
    builder.Services.ConfigureHttpJsonOptions(options =>
    {
        options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
        options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
    });
    builder.Services.AddFlightLogOpenApi();
    builder.Services.AddSignalR()
        .AddJsonProtocol(options => options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
    builder.Services.AddHostedService<StatusNotificationListener>();

    var app = builder.Build();

    await DatabaseInitializer.InitializeAsync(app.Services, app.Configuration);

    app.UseMiddleware<CorrelationIdMiddleware>();
    app.UseSerilogRequestLogging();
    app.UseExceptionHandler();
    app.UseStatusCodePages();

    if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing"))
    {
        app.MapOpenApi();
        app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "FlightLog API v1"));
    }

    app.UseAuthentication();
    app.UseRateLimiter();
    app.UseAuthorization();

    app.MapControllers();
    app.MapHub<ProcessingHub>(ProcessingHub.Path);
    app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
    app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

    await app.RunAsync();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "FlightLog API terminated unexpectedly");
    throw;
}
finally
{
    await Log.CloseAndFlushAsync();
}

/// <summary>Entry point; public so integration tests can use WebApplicationFactory.</summary>
public partial class Program;
