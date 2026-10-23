using DeviceTelemetry.Application.Abstractions.Persistence;
using DeviceTelemetry.Application.Common;
using DeviceTelemetry.Application.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DeviceTelemetry.Application.Devices.Queries.AuthenticateDevice;

/// <summary>
/// Checks a device code and key (Device Link handshake). A class rather than a record so that it has no generated
/// <c>ToString</c> that could expose <see cref="DeviceKey"/>.
/// </summary>
public sealed class AuthenticateDeviceQuery : IQuery<int>
{
    public required string? DeviceCode { get; init; }
    public required string? DeviceKey { get; init; }
}

/// <summary>Returns the internal device id, or the same <c>device.authentication_failed</c> for every kind of failure.</summary>
public sealed class AuthenticateDeviceQueryHandler(IApplicationDbContext context, ILogger<AuthenticateDeviceQueryHandler> logger)
    : IQueryHandler<AuthenticateDeviceQuery, int>
{
    public async Task<Result<int>> HandleAsync(AuthenticateDeviceQuery query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query.DeviceCode))
        {
            logger.LogWarning("Device link rejected: no device id");
            return TelemetryErrors.DeviceAuthenticationFailed;
        }

        var device = await context.Devices
            .AsNoTracking()
            .Where(d => d.DeviceCode == query.DeviceCode)
            .Select(d => new { d.Id, d.ApiKeyHash })
            .SingleOrDefaultAsync(ct);

        if (device is null)
        {
            logger.LogWarning("Device link rejected: unknown device {DeviceCode}", query.DeviceCode);
            return TelemetryErrors.DeviceAuthenticationFailed;
        }

        if (!DeviceApiKey.Verify(query.DeviceKey, device.ApiKeyHash))
        {
            logger.LogWarning(
                "Device link rejected: {Reason} for device {DeviceCode}",
                device.ApiKeyHash is null ? "no API key issued" : "invalid or missing API key",
                query.DeviceCode);
            return TelemetryErrors.DeviceAuthenticationFailed;
        }

        return device.Id;
    }
}
