using System.ComponentModel.DataAnnotations;
using System.Threading.RateLimiting;
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
            options.AddPolicy(PolicyName, PerIp<TelemetryRateLimitOptions>);
            options.AddPolicy(MobilePolicyName, PerIp<MobileRateLimitOptions>);
        });

        return services;
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
