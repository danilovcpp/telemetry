using DeviceTelemetry.Application.Abstractions.Persistence;
using DeviceTelemetry.Application.Common;
using DeviceTelemetry.Application.Devices;
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

        // Watched: the device announced a report interval or an explicit threshold, and did not pause monitoring.
        var candidates = await context.Devices
            .AsNoTracking()
            .Where(d => (d.ReportIntervalS != null || d.OfflineAfterS != null)
                        && d.MonitoringEnabled != false
                        && d.SilentSinceUtc == null
                        && d.LastSeenAtUtc != null)
            .Where(d => d.LastSeenAtUtc!.Value.AddSeconds(
                d.OfflineAfterS ?? Math.Max((d.ReportIntervalS ?? 0) * factor, minSeconds)) < nowUtc)
            .Select(d => new { d.Id, LastSeenAtUtc = d.LastSeenAtUtc!.Value, d.ReportIntervalS, d.OfflineAfterS })
            .ToListAsync(ct);

        var marked = 0;
        foreach (var candidate in candidates)
        {
            await using var transaction = await context.BeginTransactionAsync(ct);

            // Mark only if nothing changed since the candidate was read: no message arrived (LastSeenAtUtc), monitoring was
            // not paused and no other instance marked it first. A message in flight holds the row lock; this re-checks after it.
            var updated = await context.Devices
                .Where(d => d.Id == candidate.Id
                            && d.SilentSinceUtc == null
                            && d.MonitoringEnabled != false
                            && d.LastSeenAtUtc == candidate.LastSeenAtUtc)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.SilentSinceUtc, nowUtc), ct);

            if (updated == 0)
                continue; // transaction is rolled back on dispose

            var device = await context.Devices.AsNoTracking()
                .Where(d => d.Id == candidate.Id)
                .Select(DeviceSnapshot.Projection)
                .SingleAsync(ct);

            var threshold = settings.ThresholdFor(candidate.ReportIntervalS, candidate.OfflineAfterS);
            var silent = ServerEvents.Create(
                device.Id,
                TelemetryStatusCodes.DeviceSilent,
                now,
                new
                {
                    lastSeenAtUtc = candidate.LastSeenAtUtc,
                    candidate.ReportIntervalS,
                    candidate.OfflineAfterS,
                    thresholdS = (int)threshold.TotalSeconds,
                });
            context.TelemetryEvents.Add(silent);

            var notificationQueued = await notifier.TryQueueAsync(device.ToTarget(), silent, channels: null, ct);

            await context.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            marked++;

            logger.LogWarning(
                "Device {DeviceCode} is silent: last message at {LastSeenAtUtc:O}, threshold {ThresholdS}s; event {EventId} raised, notification queued: {NotificationQueued}",
                device.DeviceCode, candidate.LastSeenAtUtc, (int)threshold.TotalSeconds, silent.Id, notificationQueued);
        }

        return marked;
    }
}
