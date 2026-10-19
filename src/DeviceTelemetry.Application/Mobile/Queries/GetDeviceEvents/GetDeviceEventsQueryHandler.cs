using System.Text.Json;
using DeviceTelemetry.Application.Abstractions.Persistence;
using DeviceTelemetry.Application.Common;
using DeviceTelemetry.Application.Telemetry;
using DeviceTelemetry.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace DeviceTelemetry.Application.Mobile.Queries.GetDeviceEvents;

/// <summary>
/// Keyset pagination over (<c>OccurredAtUtc</c> DESC, <c>Id</c> DESC), served by the index
/// <c>IX_TelemetryEvents_DeviceId_OccurredAtUtc_Id</c>. Unlike OFFSET, page cost does not grow with depth,
/// and events with equal timestamps are neither skipped nor repeated because <c>Id</c> breaks ties.
/// </summary>
public sealed class GetDeviceEventsQueryHandler(IApplicationDbContext context)
    : IQueryHandler<GetDeviceEventsQuery, PagedResult<DeviceEventDto>>
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task<Result<PagedResult<DeviceEventDto>>> HandleAsync(GetDeviceEventsQuery query, CancellationToken ct)
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

        var events = context.TelemetryEvents
            .AsNoTracking()
            .Where(e => e.DeviceId == query.DeviceId);

        if (after is { } cursor)
        {
            // "Strictly after the cursor in DESC order". The redundant `<=` gives PostgreSQL an index range bound;
            // the OR only decides ties on the cursor's own timestamp.
            events = events.Where(e =>
                e.OccurredAtUtc <= cursor.OccurredAtUtc
                && (e.OccurredAtUtc < cursor.OccurredAtUtc || e.Id.CompareTo(cursor.Id) < 0));
        }

        // One extra row tells whether another page exists without a COUNT query.
        var rows = await events
            .OrderByDescending(e => e.OccurredAtUtc)
            .ThenByDescending(e => e.Id)
            .Take(query.Limit + 1)
            .Select(e => new
            {
                e.Id,
                e.StatusCode,
                e.OccurredAtUtc,
                e.ReceivedAtUtc,
                e.Payload,
                IsServer = e.Source == TelemetryEventSource.Server,
                e.Channels,
                OccurredAtRejected = e.RejectedOccurredAtUtc != null,
            })
            .ToListAsync(ct);

        // At most 32 small channel objects per event: deserializing them here keeps the response strongly typed.
        var items = rows
            .Select(e => new DeviceEventDto(
                e.Id,
                e.StatusCode,
                e.OccurredAtUtc,
                e.ReceivedAtUtc,
                e.Payload,
                e.IsServer ? "server" : "device",
                e.Channels is null ? null : JsonSerializer.Deserialize<List<TelemetryChannel>>(e.Channels, SerializerOptions),
                e.OccurredAtRejected))
            .ToList();

        // An empty page is ambiguous: no (more) events, or no such device. Only then is the device checked.
        if (items.Count == 0 && !await context.Devices.AnyAsync(d => d.Id == query.DeviceId, ct))
            return MobileErrors.DeviceNotFound;

        var hasMore = items.Count > query.Limit;
        if (hasMore)
            items.RemoveAt(items.Count - 1);

        var nextCursor = hasMore ? new EventCursor(items[^1].OccurredAtUtc, items[^1].Id).Encode() : null;
        return new PagedResult<DeviceEventDto>(items, nextCursor, hasMore);
    }
}
