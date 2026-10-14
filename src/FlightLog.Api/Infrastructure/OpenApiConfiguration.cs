using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace FlightLog.Api.Infrastructure;

internal static class OpenApiConfiguration
{
    public static IServiceCollection AddFlightLogOpenApi(this IServiceCollection services)
    {
        services.AddOpenApi("v1", options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info = new OpenApiInfo
                {
                    Title = "FlightLog Intelligence API",
                    Version = "v1",
                    Description = "Upload, archive and analyse PX4 and ArduPilot flight logs.",
                };

                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
                document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    Description = "Access token returned by POST /api/v1/auth/login.",
                };
                document.Security ??= [];
                document.Security.Add(new OpenApiSecurityRequirement
                {
                    [new OpenApiSecuritySchemeReference("Bearer", document)] = [],
                });
                return Task.CompletedTask;
            });
        });
        return services;
    }
}
