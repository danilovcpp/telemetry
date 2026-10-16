using DeviceTelemetry.Application.Abstractions.Persistence;
using DeviceTelemetry.Application.Common;
using Microsoft.EntityFrameworkCore;

namespace DeviceTelemetry.Application.Mobile.Queries.GetDeviceDetails;

public sealed class GetDeviceDetailsQueryHandler(IApplicationDbContext context)
    : IQueryHandler<GetDeviceDetailsQuery, DeviceDetailsDto>
{
    public async Task<Result<DeviceDetailsDto>> HandleAsync(GetDeviceDetailsQuery query, CancellationToken ct)
    {
        // One statement with two scalar subqueries, each a LIMIT 1 read from the head of the
        // (DeviceId, OccurredAtUtc DESC, Id DESC) index; both pick the same row because the order is unique.
        // Do not project the last event as an object: EF translates that to ROW_NUMBER() over the whole table.
        var device = await context.Devices
            .AsNoTracking()
            .Where(d => d.Id == query.DeviceId)
            .Select(d => new DeviceDetailsDto(
                d.Id,
                d.DeviceCode,
                d.Name,
                d.CustomerId,
                d.Events
                    .OrderByDescending(e => e.OccurredAtUtc)
                    .ThenByDescending(e => e.Id)
                    .Select(e => (DateTime?)e.OccurredAtUtc)
                    .FirstOrDefault(),
                d.Events
                    .OrderByDescending(e => e.OccurredAtUtc)
                    .ThenByDescending(e => e.Id)
                    .Select(e => (int?)e.StatusCode)
                    .FirstOrDefault()))
            .SingleOrDefaultAsync(ct);

        return device is null ? MobileErrors.DeviceNotFound : device;
    }
}
