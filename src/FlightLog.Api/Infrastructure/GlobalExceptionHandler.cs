using FlightLog.Application.Common;
using FlightLog.Domain.Common;
using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace FlightLog.Api.Infrastructure;

/// <summary>
/// Maps application and domain exceptions to RFC 9457 problem details. Unexpected exceptions are logged
/// and returned as a generic 500 without internal details.
/// </summary>
internal sealed partial class GlobalExceptionHandler(
    IProblemDetailsService problemDetails,
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            // The client went away; there is nobody to answer.
            httpContext.Response.StatusCode = 499;
            return true;
        }

        var problem = exception switch
        {
            ValidationException validation => ToValidationProblem(validation),
            NotFoundException notFound => Problem(StatusCodes.Status404NotFound, "Resource not found", $"The requested {notFound.Resource.ToLowerInvariant()} was not found.", "not_found"),
            ForbiddenException forbidden => Problem(StatusCodes.Status403Forbidden, "Forbidden", forbidden.Message, "forbidden"),
            ConflictException conflict => WithDetails(Problem(StatusCodes.Status409Conflict, "Conflict", conflict.Message, conflict.Code), conflict.Details),
            PayloadTooLargeException tooLarge => Problem(StatusCodes.Status413PayloadTooLarge, "File too large", tooLarge.Message, "upload.too_large"),
            BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge } => Problem(StatusCodes.Status413PayloadTooLarge, "Request too large", "The request body exceeds the allowed size.", "upload.too_large"),
            QuotaExceededException quota => Problem(StatusCodes.Status429TooManyRequests, "Limit reached", quota.Message, quota.Code),
            DomainRuleViolationException rule => Problem(StatusCodes.Status422UnprocessableEntity, "Business rule violated", rule.Message, rule.Code),
            _ => null,
        };

        if (problem is null)
        {
            LogUnhandled(logger, exception, httpContext.Request.Method, httpContext.Request.Path);
            problem = Problem(StatusCodes.Status500InternalServerError, "Unexpected error", "An unexpected error occurred. Use the trace id when reporting the problem.", "internal_error");
        }

        httpContext.Response.StatusCode = problem.Status!.Value;
        if (exception is QuotaExceededException { RetryAfter: var retryAfter })
        {
            httpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(Math.Max(1, retryAfter.TotalSeconds))).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception,
        });
    }

    private static ProblemDetails Problem(int status, string title, string detail, string code)
    {
        var problem = new ProblemDetails { Status = status, Title = title, Detail = detail };
        problem.Extensions["code"] = code;
        return problem;
    }

    private static ProblemDetails WithDetails(ProblemDetails problem, IReadOnlyDictionary<string, object?> details)
    {
        foreach (var (key, value) in details)
        {
            problem.Extensions[key] = value;
        }

        return problem;
    }

    private static ValidationProblemDetails ToValidationProblem(ValidationException exception)
    {
        var errors = exception.Errors
            .GroupBy(e => string.IsNullOrEmpty(e.PropertyName) ? string.Empty : char.ToLowerInvariant(e.PropertyName[0]) + e.PropertyName[1..])
            .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray());
        var problem = new ValidationProblemDetails(errors)
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "Validation failed",
        };
        problem.Extensions["code"] = "validation_failed";
        return problem;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception while processing {Method} {Path}")]
    private static partial void LogUnhandled(ILogger logger, Exception exception, string method, PathString path);
}
