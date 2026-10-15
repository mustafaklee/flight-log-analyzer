using FlightLog.Application.Analysis;
using FlightLog.Application.Analysis.Rules;
using FlightLog.Application.Authorization;
using FlightLog.Application.Fleet;
using FlightLog.Application.FlightData;
using FlightLog.Application.Flights;
using FlightLog.Application.Logs;
using FlightLog.Application.Messaging;
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
        services.AddScoped<IProcessingStatsService, ProcessingStatsService>();
        services.AddSingleton<ProcessingMetrics>();
        services.AddSingleton<WorkRunner>();
        services.AddScoped<IOutboxMaintenance, OutboxMaintenance>();

        // Analysis rules: stateless, one instance each. A new rule is registered here and versioned in its descriptor.
        services.AddSingleton<IAnalysisRule, GpsFixLossRule>();
        services.AddSingleton<IAnalysisRule, GpsQualityRule>();
        services.AddSingleton<IAnalysisRule, EkfInnovationRule>();
        services.AddSingleton<IAnalysisRule, EkfMessagesRule>();
        services.AddSingleton<IAnalysisRule, VibrationLevelRule>();
        services.AddSingleton<IAnalysisRule, AccelerometerClippingRule>();
        services.AddSingleton<IAnalysisRule, BatteryVoltageRule>();
        services.AddSingleton<IAnalysisRule, BatteryRemainingRule>();
        services.AddSingleton<IAnalysisRule, FailsafeRule>();
        services.AddSingleton<IAnalysisRule, ErrorEventsRule>();
        services.AddSingleton<IAnalysisRule, ModeTransitionRule>();
        services.AddSingleton<IAnalysisRule, DataGapRule>();
        services.AddSingleton<IAnalysisRule, ParameterChangeRule>();
        services.AddSingleton<RuleEngine>();
        services.AddScoped<IAnalysisService, AnalysisService>();
        services.AddScoped<IAnalysisRunService, AnalysisRunService>();
        services.AddScoped<Ai.IAiAnalysisService, Ai.AiAnalysisService>();
        services.AddScoped<Ai.IAiAnalysisAppService, Ai.AiAnalysisAppService>();
        services.AddScoped<Ai.IAiUsageService, Ai.AiUsageService>();
        services.AddScoped<Dashboard.IDashboardService, Dashboard.DashboardService>();
        return services;
    }
}
