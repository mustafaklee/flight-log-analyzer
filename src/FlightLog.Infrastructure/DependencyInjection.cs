using Amazon.Runtime;
using Amazon.S3;
using FlightLog.Application.Abstractions;
using FlightLog.Application.Ai;
using FlightLog.Application.Logs;
using FlightLog.Application.Messaging;
using FlightLog.Application.Processing;
using FlightLog.Infrastructure.Ai;
using FlightLog.Infrastructure.Identity;
using FlightLog.Infrastructure.Messaging;
using FlightLog.Infrastructure.Persistence;
using FlightLog.Infrastructure.Processing;
using FlightLog.Infrastructure.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FlightLog.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("Connection string 'Postgres' is not configured.");

        services.Configure<MessagingOptions>(configuration.GetSection(MessagingOptions.SectionName));
        services.AddSingleton<OutboxInterceptor>();
        services.AddSingleton<StatusNotificationInterceptor>();
        services.AddDbContext<FlightLogDbContext>((sp, options) => options
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsAssembly(typeof(FlightLogDbContext).Assembly.FullName))
            .AddInterceptors(sp.GetRequiredService<OutboxInterceptor>(), sp.GetRequiredService<StatusNotificationInterceptor>()));
        services.AddScoped<IApplicationDbContext>(sp => sp.GetRequiredService<FlightLogDbContext>());

        services.AddIdentityCore<ApplicationUser>(options =>
            {
                options.User.RequireUniqueEmail = true;
                options.Password.RequiredLength = 10;
                options.Password.RequireNonAlphanumeric = false;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                options.Lockout.AllowedForNewUsers = true;
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<FlightLogDbContext>()
            .AddSignInManager()
            .AddDefaultTokenProviders();

        // Bearer tokens are protected with Data Protection; keys live in PostgreSQL so that tokens survive
        // restarts and are valid across API instances.
        services.AddDataProtection()
            .SetApplicationName("FlightLog")
            .PersistKeysToDbContext<FlightLogDbContext>();

        services.Configure<BootstrapAdministratorOptions>(configuration.GetSection(BootstrapAdministratorOptions.SectionName));
        services.AddScoped<AdministratorSeeder>();
        services.AddScoped<IUserDirectory, UserDirectory>();
        services.AddSingleton(TimeProvider.System);

        services.Configure<UploadOptions>(configuration.GetSection(UploadOptions.SectionName));
        services.Configure<ProcessingOptions>(configuration.GetSection(ProcessingOptions.SectionName));
        services.AddMetrics();
        services.AddSingleton<ITransientErrorClassifier, TransientErrorClassifier>();

        // AI analysis (ADR-0008): only the worker calls the provider; the key is only given to the worker.
        services.Configure<AiOptions>(configuration.GetSection(AiOptions.SectionName));
        services.Configure<Application.Collaboration.AuditOptions>(configuration.GetSection(Application.Collaboration.AuditOptions.SectionName));
        services.AddSingleton<IFlightAnalysisProvider>(sp =>
        {
            var ai = sp.GetRequiredService<IOptions<AiOptions>>();
            return string.Equals(ai.Value.Provider, "Anthropic", StringComparison.OrdinalIgnoreCase)
                ? new AnthropicFlightAnalysisProvider(ai)
                : new UnknownFlightAnalysisProvider(ai.Value.Provider, ai.Value.Model);
        });

        AddStorage(services, configuration);

        var health = services.AddHealthChecks()
            .AddDbContextCheck<FlightLogDbContext>("postgres", tags: ["ready"])
            .AddCheck<StorageHealthCheck>("object-storage", tags: ["ready"]);

        var transport = configuration.GetValue<MessageTransport?>($"{MessagingOptions.SectionName}:{nameof(MessagingOptions.Transport)}") ?? MessageTransport.Database;
        var processing = configuration.GetValue<bool>($"{ProcessingOptions.SectionName}:{nameof(ProcessingOptions.Enabled)}");
        if (transport == MessageTransport.RabbitMq)
        {
            services.Configure<RabbitMqOptions>(configuration.GetSection(RabbitMqOptions.SectionName));
            services.AddSingleton<RabbitMqConnection>();
            services.AddSingleton<IMessageQueueMonitor, RabbitMqQueueMonitor>();

            // Every process that writes outbox rows also forwards them.
            services.AddHostedService<OutboxPublisher>();
            health.AddCheck<RabbitMqHealthCheck>("rabbitmq", tags: ["ready"]);
            if (processing)
            {
                services.AddHostedService<RabbitMqWorker>();
                services.AddHostedService<MaintenanceWorker>();
            }
        }
        else
        {
            services.AddSingleton<IMessageQueueMonitor, NoBrokerQueueMonitor>();
            if (processing)
            {
                services.AddHostedService<DatabasePollingWorker>();
            }
        }

        return services;
    }

    private static void AddStorage(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<StorageOptions>(configuration.GetSection(StorageOptions.SectionName));
        services.AddSingleton<IAmazonS3>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<StorageOptions>>().Value;
            var config = new AmazonS3Config
            {
                ForcePathStyle = options.ForcePathStyle,
                // S3 compatible stores do not all support the SDK v4 default flexible checksums.
                RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
                ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            };
            if (!string.IsNullOrWhiteSpace(options.ServiceUrl))
            {
                config.ServiceURL = options.ServiceUrl;
                config.AuthenticationRegion = options.Region;
            }
            else
            {
                config.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(options.Region);
            }

            return string.IsNullOrWhiteSpace(options.AccessKey)
                ? new AmazonS3Client(config)
                : new AmazonS3Client(new BasicAWSCredentials(options.AccessKey, options.SecretKey), config);
        });
        services.AddSingleton<S3FileStorage>();
        services.AddSingleton<IFileStorage>(sp => sp.GetRequiredService<S3FileStorage>());
    }
}
