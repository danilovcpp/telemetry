using System.Text.Json;
using DeviceTelemetry.Application.Abstractions.Persistence;
using DeviceTelemetry.Application.Common;
using DeviceTelemetry.Application.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DeviceTelemetry.Application.Devices.Commands.UpdateDeviceSettings;

/// <summary>
/// Applies the settings a device sends and returns what is stored. Invalid values never fail the request: they are listed
/// in <see cref="DeviceSettingsResult.Rejected"/> and the stored value stays. Counts as contact with the device (ends a
/// silence episode); a changed notification address raises 190003 and notifies the previous address.
/// <c>"notifyEmail": null</c> switches emails off (<see cref="DeviceSettingsInput.DisableNotifications"/>).
/// Idempotent: repeating the same request changes nothing but the last-contact time.
/// </summary>
public sealed class UpdateDeviceSettingsCommandHandler(
    IApplicationDbContext context,
    DeviceContactRecorder contactRecorder,
    TimeProvider timeProvider,
    ILogger<UpdateDeviceSettingsCommandHandler> logger)
    : ICommandHandler<UpdateDeviceSettingsCommand, DeviceSettingsResult>
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task<Result<DeviceSettingsResult>> HandleAsync(UpdateDeviceSettingsCommand command, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command.DeviceId))
            return TelemetryErrors.Validation("'deviceId' is required.");

        var device = await context.Devices
            .AsNoTracking()
            .Where(d => d.DeviceCode == command.DeviceId)
            .Select(DeviceSnapshot.Projection)
            .SingleOrDefaultAsync(ct);

        if (device is null || !DeviceApiKey.Verify(command.DeviceKey, device.ApiKeyHash))
        {
            logger.LogWarning("Settings rejected: unknown device or invalid key for {DeviceCode}", command.DeviceId);
            return TelemetryErrors.DeviceAuthenticationFailed;
        }

        var settings = DeviceSettingsResolver.Resolve(command.Settings);
        foreach (var (field, reason) in settings.Rejected)
            logger.LogWarning("Settings from device {DeviceCode}: {Field} ignored ({Reason})", device.DeviceCode, field, reason);

        await using (var transaction = await context.BeginTransactionAsync(ct))
        {
            await contactRecorder.RecordAsync(device, settings, timeProvider.GetUtcNow(), ct);
            await context.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }

        var stored = await context.Devices
            .AsNoTracking()
            .Where(d => d.Id == device.Id)
            .Select(d => new
            {
                d.Name, d.NotifyEmail, d.NotifyLanguage, d.TimeZone, d.ReportIntervalS, d.OfflineAfterS, d.MonitoringEnabled, d.Channels,
                d.NotificationsDisabled, CustomerEmail = d.Customer.Email,
            })
            .SingleAsync(ct);

        logger.LogInformation(
            "Settings of device {DeviceCode} updated; rejected: {Rejected}",
            device.DeviceCode, settings.Rejected.Count == 0 ? "none" : string.Join(", ", settings.Rejected.Keys));

        return new DeviceSettingsResult(
            new DeviceSettingsDto(
                stored.Name,
                stored.NotifyEmail,
                stored.NotifyLanguage,
                stored.TimeZone,
                stored.ReportIntervalS,
                stored.OfflineAfterS,
                stored.MonitoringEnabled != false,
                stored.Channels is null ? null : JsonSerializer.Deserialize<List<TelemetryChannel>>(stored.Channels, SerializerOptions),
                stored.NotificationsDisabled),
            settings.Rejected,
            // Read back after the commit: the answer confirms what is in effect, not what was asked for.
            stored.NotificationsDisabled ? null : stored.NotifyEmail ?? stored.CustomerEmail);
    }
}
