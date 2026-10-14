using FlightLog.Application.Abstractions;
using FlightLog.Infrastructure.Identity;
using FlightLog.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FlightLog.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("Connection string 'Postgres' is not configured.");

        services.AddDbContext<FlightLogDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsAssembly(typeof(FlightLogDbContext).Assembly.FullName)));
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

        services.AddHealthChecks()
            .AddDbContextCheck<FlightLogDbContext>("postgres", tags: ["ready"]);

        return services;
    }
}
