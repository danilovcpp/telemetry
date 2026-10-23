using System.Text.Json;
using DeviceTelemetry.Application.Abstractions.Persistence;
using DeviceTelemetry.Application.Common;
using DeviceTelemetry.Application.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DeviceTelemetry.Application.Mobile.Queries.GetDeviceDetails;

public sealed class GetDeviceDetailsQueryHandler(IApplicationDbContext context, IOptions<MobileFeatureOptions> featureOptions)
    : IQueryHandler<GetDeviceDetailsQuery, DeviceDetailsDto>
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task<Result<DeviceDetailsDto>> HandleAsync(GetDeviceDetailsQuery query, CancellationToken ct)
    {
        // One statement with two scalar subqueries, each a LIMIT 1 read from the head of the
        // (DeviceId, OccurredAtUtc DESC, Id DESC) index; both pick the same row because the order is unique.
        // Do not project the last event as an object: EF translates that to ROW_NUMBER() over the whole table.
        var row = await context.Devices
            .AsNoTracking()
            .Where(d => d.Id == query.DeviceId)
            .Select(d => new
            {
                Details = new DeviceDetailsDto(
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
                        .FirstOrDefault(),
                    d.LastSeenAtUtc,
                    d.ReportIntervalS,
                    d.SilentSinceUtc,
                    d.OfflineAfterS,
                    d.MonitoringEnabled != false,
                    d.TimeZone,
                    null,
                    null!),
                d.Channels,
                d.LinkHello,
                HasCopies = d.LogFiles.Any(f => f.StoredSize > 0 && !f.Deleted),
            })
            .SingleOrDefaultAsync(ct);

        if (row is null)
            return MobileErrors.DeviceNotFound;

        // The channel catalog is a small jsonb list; deserializing it here keeps the response strongly typed.
        return row.Details with
        {
            Channels = row.Channels is null
                ? null
                : JsonSerializer.Deserialize<List<TelemetryChannel>>(row.Channels, SerializerOptions),
            Features = MobileFeatures.For(featureOptions.Value, row.LinkHello, row.HasCopies),
        };
    }
}
