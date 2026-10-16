using System.ComponentModel.DataAnnotations;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace DeviceTelemetry.Api.Http;

public sealed class TelemetryRateLimitOptions
{
    public const string SectionName = "RateLimiting:Telemetry";

    /// <summary>Requests allowed per client IP address within <see cref="Window"/>.</summary>
    [Range(1, 1_000_000)]
    public int PermitLimit { get; set; } = 600;

    [Range(typeof(TimeSpan), "00:00:01", "01:00:00")]
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);
}

public static class TelemetryRateLimiting
{
    public const string PolicyName = "telemetry";

    /// <summary>
    /// Fixed-window limit per client IP. Behind a reverse proxy, configure forwarded headers so the
    /// real client address is used; otherwise all devices share the proxy's partition.
    /// </summary>
    public static IServiceCollection AddTelemetryRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<TelemetryRateLimitOptions>()
            .Bind(configuration.GetSection(TelemetryRateLimitOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(PolicyName, httpContext =>
            {
                var settings = httpContext.RequestServices.GetRequiredService<IOptions<TelemetryRateLimitOptions>>().Value;
                return RateLimitPartition.GetFixedWindowLimiter(
                    httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = settings.PermitLimit,
                        Window = settings.Window,
                        QueueLimit = 0,
                    });
            });
        });

        return services;
    }
}
