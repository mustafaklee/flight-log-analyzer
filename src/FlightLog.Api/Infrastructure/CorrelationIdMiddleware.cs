using Serilog.Context;

namespace FlightLog.Api.Infrastructure;

/// <summary>
/// Accepts a caller supplied <c>X-Correlation-Id</c> (or generates one), echoes it on the response
/// and attaches it to every log event of the request.
/// </summary>
internal sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-Id";
    public const string ItemKey = "FlightLog.CorrelationId";
    private const int MaxLength = 64;

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers[HeaderName].ToString();
        if (string.IsNullOrWhiteSpace(correlationId) || correlationId.Length > MaxLength || !correlationId.All(IsSafe))
        {
            correlationId = context.TraceIdentifier;
        }

        context.Items[ItemKey] = correlationId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await next(context);
        }
    }

    private static bool IsSafe(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':';
}
