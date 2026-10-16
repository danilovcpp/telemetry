using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace DeviceTelemetry.Api.Http;

/// <summary>
/// Returns the status carried by <see cref="BadHttpRequestException"/> (e.g. 413 when the body exceeds
/// <c>Kestrel:Limits:MaxRequestBodySize</c>) instead of a generic 500.
/// </summary>
public sealed class BadHttpRequestExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<BadHttpRequestExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken ct)
    {
        if (exception is not BadHttpRequestException badRequest)
            return false;

        logger.LogWarning("Rejected malformed request {Method} {Path}: {StatusCode} {Reason}",
            httpContext.Request.Method, httpContext.Request.Path, badRequest.StatusCode, badRequest.Message);

        httpContext.Response.StatusCode = badRequest.StatusCode;
        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails { Status = badRequest.StatusCode, Detail = badRequest.Message },
        });
    }
}
