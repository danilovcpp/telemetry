using DeviceTelemetry.Application.Abstractions.Persistence;
using DeviceTelemetry.Application.Notifications;
using DeviceTelemetry.Application.Telemetry;
using DeviceTelemetry.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DeviceTelemetry.Application.Devices;

/// <summary>
/// Records that a device made contact (any accepted message: telemetry or settings) and applies the settings it sent.
/// Must run inside the caller's transaction (<see cref="IApplicationDbContext.BeginTransactionAsync"/>); events and
/// outbox messages it creates are added to the context and committed by the caller.
/// <list type="bullet">
/// <item>Updates <c>LastSeenAtUtc</c> and the settings in one statement, which also locks the device row until commit:
/// the silence monitor cannot mark the device silent meanwhile, and concurrent messages cannot both end an episode.</item>
/// <item>Ends a silence episode: clears <c>SilentSinceUtc</c> and raises 190002.</item>
/// <item>When the effective notification address changes (including emails switched off or on by the device), raises
/// 190003 and emails the <em>previous</em> address.</item>
/// </list>
/// </summary>
public sealed class DeviceContactRecorder(
    IApplicationDbContext context,
    TelemetryNotifier notifier,
    ILogger<DeviceContactRecorder> logger)
{
    /// <returns>The device as it is after the update, for addressing the caller's own notifications.</returns>
    public async Task<DeviceSnapshot> RecordAsync(
        DeviceSnapshot device, ResolvedDeviceSettings settings, DateTimeOffset now, CancellationToken ct)
    {
        var nowUtc = now.UtcDateTime;

        await context.Devices
            .Where(d => d.Id == device.Id)
            .ExecuteUpdateAsync(s =>
            {
                s.SetProperty(d => d.LastSeenAtUtc, nowUtc);
                if (settings.NotifyEmail.IsSet) s.SetProperty(d => d.NotifyEmail, settings.NotifyEmail.Value);
                if (settings.NotificationsDisabled.IsSet) s.SetProperty(d => d.NotificationsDisabled, settings.NotificationsDisabled.Value);
                if (settings.NotifyLanguage.IsSet) s.SetProperty(d => d.NotifyLanguage, settings.NotifyLanguage.Value);
                if (settings.ReportIntervalS.IsSet) s.SetProperty(d => d.ReportIntervalS, settings.ReportIntervalS.Value);
                if (settings.Name.IsSet) s.SetProperty(d => d.Name, settings.Name.Value);
                if (settings.TimeZone.IsSet) s.SetProperty(d => d.TimeZone, settings.TimeZone.Value);
                if (settings.OfflineAfterS.IsSet) s.SetProperty(d => d.OfflineAfterS, settings.OfflineAfterS.Value);
                if (settings.Monitoring.IsSet) s.SetProperty(d => d.MonitoringEnabled, settings.Monitoring.Value);
                if (settings.ChannelsJson.IsSet) s.SetProperty(d => d.Channels, settings.ChannelsJson.Value);
            }, ct);

        var updated = device.With(settings);

        var silentSinceUtc = await context.Devices.Where(d => d.Id == device.Id).Select(d => d.SilentSinceUtc).SingleAsync(ct);
        if (silentSinceUtc is not null)
        {
            await context.Devices
                .Where(d => d.Id == device.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.SilentSinceUtc, (DateTime?)null), ct);

            var backOnline = ServerEvents.Create(device.Id, TelemetryStatusCodes.DeviceBackOnline, now, new { silentSinceUtc });
            context.TelemetryEvents.Add(backOnline);
            await notifier.TryQueueAsync(updated.ToTarget(), backOnline, channels: null, ct);

            logger.LogInformation(
                "Device {DeviceCode} reports again after silence since {SilentSinceUtc:O}; event {EventId} raised",
                device.DeviceCode, silentSinceUtc, backOnline.Id);
        }

        if (!string.Equals(device.EffectiveEmail, updated.EffectiveEmail, StringComparison.OrdinalIgnoreCase))
        {
            // No addresses in the payload: event payloads are visible through the public mobile API.
            var changed = ServerEvents.Create(device.Id, TelemetryStatusCodes.NotifyEmailChanged, now,
                new
                {
                    setting = "notifyEmail",
                    usesCustomerEmail = !updated.NotificationsDisabled && updated.NotifyEmail is null,
                    notificationsDisabled = updated.NotificationsDisabled,
                });
            context.TelemetryEvents.Add(changed);

            // Tell the previous recipient (in the device's current language) where notifications go now.
            // Also when the device switched emails off: the last email says so, so that a silent switch-off is noticed.
            var previousRecipient = updated.ToTarget() with
            {
                DeviceNotifyEmail = device.EffectiveEmail, CustomerEmail = null, NotificationsDisabled = false,
            };
            var newAddress = updated.EffectiveEmail ?? NotificationLanguages.NoRecipientText(previousRecipient.Language);
            var queued = await notifier.TryQueueAsync(previousRecipient, changed, channels: null, ct, notifyEmail: newAddress);

            logger.LogWarning(
                "Device {DeviceCode} changed its notification address; event {EventId} raised, previous recipient notified: {Notified}",
                device.DeviceCode, changed.Id, queued);
        }

        return updated;
    }
}
