using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace FlightLog.Api.Infrastructure;

internal static class OpenApiConfiguration
{
    public static IServiceCollection AddFlightLogOpenApi(this IServiceCollection services)
    {
        services.AddOpenApi("v1", options =>
        {
            // Shared enum schemas are referenced by nullable and non-nullable properties alike; nullability is
            // already expressed at the property ("oneOf: null | enum"), so the enum itself lists only real values.
            options.AddSchemaTransformer((schema, _, _) =>
            {
                if (schema.Enum is { Count: > 0 } values)
                {
                    for (var i = values.Count - 1; i >= 0; i--)
                    {
                        if (values[i] is null)
                        {
                            values.RemoveAt(i);
                        }
                    }
                }

                return Task.CompletedTask;
            });
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

/// <summary>
/// Documents the success response of actions that only declare error responses. ApiExplorer infers the response
/// body from the return type only for actions without any [ProducesResponseType]; the class-level error
/// attributes would otherwise hide the 200 response and its schema from the OpenAPI document (and the web client).
/// </summary>
internal sealed class SuccessResponseConvention : Microsoft.AspNetCore.Mvc.ApplicationModels.IActionModelConvention
{
    public void Apply(Microsoft.AspNetCore.Mvc.ApplicationModels.ActionModel action)
    {
        var declared = action.Filters.OfType<Microsoft.AspNetCore.Mvc.ApiExplorer.IApiResponseMetadataProvider>()
            .Concat(action.Controller.Filters.OfType<Microsoft.AspNetCore.Mvc.ApiExplorer.IApiResponseMetadataProvider>());
        if (!declared.Any(d => d.StatusCode is >= 200 and < 300))
        {
            action.Filters.Add(new Microsoft.AspNetCore.Mvc.ProducesResponseTypeAttribute(StatusCodes.Status200OK));
        }
    }
}
