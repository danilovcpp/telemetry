using DeviceTelemetry.Application.Abstractions.Persistence;
using DeviceTelemetry.Application.Common;
using DeviceTelemetry.Application.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace DeviceTelemetry.Application.Mobile.Queries.GetDeviceState;

public sealed class GetDeviceStateQueryHandler(IApplicationDbContext context)
    : IQueryHandler<GetDeviceStateQuery, DeviceStateDto>
{
    public async Task<Result<DeviceStateDto>> HandleAsync(GetDeviceStateQuery query, CancellationToken ct)
    {
        var device = await context.Devices
            .AsNoTracking()
            .Where(d => d.Id == query.DeviceId)
            .Select(d => new { d.Id, d.LastSeenAtUtc, d.ReportIntervalS, d.SilentSinceUtc })
            .SingleOrDefaultAsync(ct);

        if (device is null)
            return MobileErrors.DeviceNotFound;

        // LIMIT 1 on IX_TelemetryEvents_DeviceId_StatusCode_OccurredAtUtc_Id, even for devices that never send reports.
        var lastReport = await context.TelemetryEvents
            .AsNoTracking()
            .Where(e => e.DeviceId == query.DeviceId && e.StatusCode == TelemetryStatusCodes.RegularReport)
            .OrderByDescending(e => e.OccurredAtUtc)
            .ThenByDescending(e => e.Id)
            .Select(e => new DeviceReportDto(e.Id, e.OccurredAtUtc, e.ReceivedAtUtc, e.Payload))
            .FirstOrDefaultAsync(ct);

        return new DeviceStateDto(device.Id, device.LastSeenAtUtc, device.ReportIntervalS, device.SilentSinceUtc, lastReport);
    }
}
