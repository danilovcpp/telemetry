using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace DeviceTelemetry.Api.Http;

/// <summary>Fixed-window limit per client IP for one rate limiter policy.</summary>
public class RateLimitOptions
{
    /// <summary>Requests allowed per client IP address within <see cref="Window"/>.</summary>
    [Range(1, 1_000_000)]
    public int PermitLimit { get; set; } = 600;

    [Range(typeof(TimeSpan), "00:00:01", "01:00:00")]
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);
}

/// <summary>Device telemetry ingestion (section <c>RateLimiting:Telemetry</c>).</summary>
public sealed class TelemetryRateLimitOptions : RateLimitOptions
{
    public const string SectionName = "RateLimiting:Telemetry";
}

/// <summary>Public mobile read API (section <c>RateLimiting:Mobile</c>); also slows down enumeration of ids.</summary>
public sealed class MobileRateLimitOptions : RateLimitOptions
{
    public const string SectionName = "RateLimiting:Mobile";

    public MobileRateLimitOptions() => PermitLimit = 120;
}

public static class TelemetryRateLimiting
{
    public const string PolicyName = "telemetry";
    public const string MobilePolicyName = "mobile";

    /// <summary>
    /// Fixed-window limits per client IP. Behind a reverse proxy, configure forwarded headers so the
    /// real client address is used; otherwise all clients share the proxy's partition.
    /// </summary>
    public static IServiceCollection AddTelemetryRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<TelemetryRateLimitOptions>()
            .Bind(configuration.GetSection(TelemetryRateLimitOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<MobileRateLimitOptions>()
            .Bind(configuration.GetSection(MobileRateLimitOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = WriteRejectionAsync;
            options.AddPolicy(PolicyName, PerIp<TelemetryRateLimitOptions>);
            options.AddPolicy(MobilePolicyName, PerIp<MobileRateLimitOptions>);
        });

        return services;
    }

    /// <summary>429 with <c>Retry-After</c> (seconds until the window resets) and the usual problem body with a <c>code</c>.</summary>
    private static async ValueTask WriteRejectionAsync(OnRejectedContext context, CancellationToken ct)
    {
        var response = context.HttpContext.Response;
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);

        await context.HttpContext.RequestServices.GetRequiredService<IProblemDetailsService>().TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context.HttpContext,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status429TooManyRequests,
                Detail = "Too many requests from this address. Retry after the number of seconds in the Retry-After header.",
                Extensions = { ["code"] = "rate_limit.exceeded" },
            },
        });
    }

    private static RateLimitPartition<string> PerIp<TOptions>(HttpContext httpContext)
        where TOptions : RateLimitOptions
    {
        var settings = httpContext.RequestServices.GetRequiredService<IOptions<TOptions>>().Value;
        return RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = settings.PermitLimit,
                Window = settings.Window,
                QueueLimit = 0,
            });
    }
}
