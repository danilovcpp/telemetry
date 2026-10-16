using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace DeviceTelemetry.Api.Http;

/// <summary>
/// Maps transient database failures to <c>503 Service Unavailable</c> with <c>Retry-After</c>, so devices retry
/// instead of treating the message as rejected. All other exceptions fall through to the generic 500 problem response.
/// </summary>
public sealed class DatabaseUnavailableExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<DatabaseUnavailableExceptionHandler> logger) : IExceptionHandler
{
    private const int RetryAfterSeconds = 30;

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken ct)
    {
        if (!IsTransientDatabaseFailure(exception))
            return false;

        logger.LogError(exception, "Database unavailable while handling {Method} {Path}",
            httpContext.Request.Method, httpContext.Request.Path);

        httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        httpContext.Response.Headers.RetryAfter = RetryAfterSeconds.ToString();

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Detail = "The service is temporarily unavailable. Retry the request later.",
                Extensions = { ["code"] = "service.unavailable" },
            },
        });
    }

    private static bool IsTransientDatabaseFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is NpgsqlException { IsTransient: true })
                return true;
        }

        return false;
    }
}
