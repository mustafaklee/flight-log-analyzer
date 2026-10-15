using FlightLog.Application.Authorization;
using FlightLog.Application.Fleet;
using FlightLog.Application.FlightData;
using FlightLog.Application.Flights;
using FlightLog.Application.Logs;
using FlightLog.Application.Processing;
using FlightLog.Application.Projects;
using FlightLog.Application.Teams;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace FlightLog.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly, includeInternalTypes: true);
        services.AddScoped<ITeamAccessService, TeamAccessService>();
        services.AddScoped<ITeamService, TeamService>();
        services.AddScoped<IProjectService, ProjectService>();
        services.AddScoped<IAircraftService, AircraftService>();
        services.AddScoped<IFlightService, FlightService>();
        services.AddScoped<ILogFileService, LogFileService>();
        services.AddScoped<ILogProcessingService, LogProcessingService>();
        services.AddScoped<IFlightDataService, FlightDataService>();
        services.AddSingleton<LogProcessingRunner>();
        return services;
    }
}
