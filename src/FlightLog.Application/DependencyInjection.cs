using FlightLog.Application.Authorization;
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
        return services;
    }
}
