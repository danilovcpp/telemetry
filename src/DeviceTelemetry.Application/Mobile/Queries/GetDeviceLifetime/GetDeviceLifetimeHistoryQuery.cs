using DeviceTelemetry.Application.Abstractions.Persistence;
using DeviceTelemetry.Application.Common;
using DeviceTelemetry.Application.Devices;
using DeviceTelemetry.Application.Mobile.Queries.GetDeviceEvents;
using Microsoft.EntityFrameworkCore;

namespace DeviceTelemetry.Application.Mobile.Queries.GetDeviceLifetime;

/// <summary>History of a device's lifetime counters, newest first: one point per report in which a counter other than the
/// powered minutes changed.</summary>
public sealed record GetDeviceLifetimeHistoryQuery(int DeviceId, int Limit = GetDeviceEventsQuery.DefaultLimit, string? Cursor = null)
    : IQuery<PagedResult<DeviceLifetimePointDto>>;

/// <param name="AtUtc">Device time of the report (<c>occurredAtUtc</c> of the event).</param>
/// <param name="EventId">The report the counters came from (an event of the device history).</param>
/// <param name="Lifetime">The counters at that time.</param>
public sealed record DeviceLifetimePointDto(DateTime AtUtc, Guid EventId, DeviceLifetime Lifetime);

/// <summary>Keyset pagination over (<c>OccurredAtUtc</c> DESC, <c>EventId</c> DESC), with the event history's cursor format.</summary>
public sealed class GetDeviceLifetimeHistoryQueryHandler(IApplicationDbContext context)
    : IQueryHandler<GetDeviceLifetimeHistoryQuery, PagedResult<DeviceLifetimePointDto>>
{
    public async Task<Result<PagedResult<DeviceLifetimePointDto>>> HandleAsync(GetDeviceLifetimeHistoryQuery query, CancellationToken ct)
    {
        if (query.Limit is < 1 or > GetDeviceEventsQuery.MaxLimit)
            return MobileErrors.InvalidLimit(GetDeviceEventsQuery.MaxLimit);

        EventCursor? after = null;
        if (query.Cursor is not null)
        {
            if (!EventCursor.TryDecode(query.Cursor, out var decoded))
                return MobileErrors.InvalidCursor;
            after = decoded;
        }

        var points = context.DeviceLifetimeSnapshots.AsNoTracking().Where(s => s.DeviceId == query.DeviceId);
        if (after is { } cursor)
        {
            points = points.Where(s =>
                s.OccurredAtUtc <= cursor.OccurredAtUtc
                && (s.OccurredAtUtc < cursor.OccurredAtUtc || s.EventId.CompareTo(cursor.Id) < 0));
        }

        var rows = await points
            .OrderByDescending(s => s.OccurredAtUtc)
            .ThenByDescending(s => s.EventId)
            .Take(query.Limit + 1)
            .Select(s => new { s.OccurredAtUtc, s.EventId, s.Lifetime })
            .ToListAsync(ct);

        if (rows.Count == 0 && !await context.Devices.AnyAsync(d => d.Id == query.DeviceId, ct))
            return MobileErrors.DeviceNotFound;

        var hasMore = rows.Count > query.Limit;
        if (hasMore)
            rows.RemoveAt(rows.Count - 1);

        // Stored rows always hold a JSON object (DeviceLifetimeRecorder checks it).
        var items = rows
            .Select(r => new DeviceLifetimePointDto(r.OccurredAtUtc, r.EventId, DeviceLifetime.Parse(r.Lifetime)!))
            .ToList();

        var nextCursor = hasMore ? new EventCursor(items[^1].AtUtc, items[^1].EventId).Encode() : null;
        return new PagedResult<DeviceLifetimePointDto>(items, nextCursor, hasMore);
    }
}
