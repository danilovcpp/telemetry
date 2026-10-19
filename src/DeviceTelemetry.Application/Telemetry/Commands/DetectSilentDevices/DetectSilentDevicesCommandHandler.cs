using DeviceTelemetry.Application.Abstractions.Persistence;
using DeviceTelemetry.Application.Common;
using DeviceTelemetry.Application.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DeviceTelemetry.Application.Telemetry.Commands.DetectSilentDevices;

/// <summary>
/// A device is watched only when it reports <c>ReportIntervalS</c>; it is silent when no message arrived for
/// max(<see cref="SilenceMonitorOptions.ThresholdIntervals"/> × interval, <see cref="SilenceMonitorOptions.MinThreshold"/>),
/// measured on the server clock (<c>LastSeenAtUtc</c>). One 190001 per silence episode: <c>SilentSinceUtc</c> marks the
/// episode until the next device message ends it (see the telemetry handler, which raises 190002).
/// </summary>
/// <returns>The number of devices newly marked silent.</returns>
public sealed class DetectSilentDevicesCommandHandler(
    IApplicationDbContext context,
    TelemetryNotifier notifier,
    IOptions<SilenceMonitorOptions> options,
    TimeProvider timeProvider,
    ILogger<DetectSilentDevicesCommandHandler> logger)
    : ICommandHandler<DetectSilentDevicesCommand, int>
{
    public async Task<Result<int>> HandleAsync(DetectSilentDevicesCommand command, CancellationToken ct)
    {
        var settings = options.Value;
        var now = timeProvider.GetUtcNow();
        var nowUtc = now.UtcDateTime;
        var factor = settings.ThresholdIntervals;
        var minSeconds = (int)settings.MinThreshold.TotalSeconds;

        var candidates = await context.Devices
            .AsNoTracking()
            .Where(d => d.ReportIntervalS != null && d.SilentSinceUtc == null && d.LastSeenAtUtc != null)
            .Where(d => d.LastSeenAtUtc!.Value.AddSeconds(Math.Max(d.ReportIntervalS!.Value * factor, minSeconds)) < nowUtc)
            .Select(d => new
            {
                d.Id,
                d.DeviceCode,
                d.Name,
                d.NotifyEmail,
                LastSeenAtUtc = d.LastSeenAtUtc!.Value,
                ReportIntervalS = d.ReportIntervalS!.Value,
                CustomerName = d.Customer.Name,
                CustomerEmail = d.Customer.Email,
            })
            .ToListAsync(ct);

        var marked = 0;
        foreach (var device in candidates)
        {
            await using var transaction = await context.BeginTransactionAsync(ct);

            // Mark only if nothing changed since the candidate was read: no message arrived (LastSeenAtUtc) and no other
            // instance marked it first. A message in flight holds the row lock, and this re-checks after it commits.
            var updated = await context.Devices
                .Where(d => d.Id == device.Id && d.SilentSinceUtc == null && d.LastSeenAtUtc == device.LastSeenAtUtc)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.SilentSinceUtc, nowUtc), ct);

            if (updated == 0)
                continue; // transaction is rolled back on dispose

            var threshold = settings.ThresholdFor(device.ReportIntervalS);
            var silent = ServerEvents.Create(
                device.Id,
                TelemetryStatusCodes.DeviceSilent,
                now,
                new { lastSeenAtUtc = device.LastSeenAtUtc, device.ReportIntervalS, thresholdS = (int)threshold.TotalSeconds });
            context.TelemetryEvents.Add(silent);

            var notificationQueued = await notifier.TryQueueAsync(
                new NotificationTarget(device.DeviceCode, device.Name, device.CustomerName, device.NotifyEmail, device.CustomerEmail),
                silent,
                channels: null,
                ct);

            await context.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            marked++;

            logger.LogWarning(
                "Device {DeviceCode} is silent: last message at {LastSeenAtUtc:O}, interval {ReportIntervalS}s, threshold {ThresholdS}s; event {EventId} raised, notification queued: {NotificationQueued}",
                device.DeviceCode, device.LastSeenAtUtc, device.ReportIntervalS, (int)threshold.TotalSeconds, silent.Id, notificationQueued);
        }

        return marked;
    }
}
