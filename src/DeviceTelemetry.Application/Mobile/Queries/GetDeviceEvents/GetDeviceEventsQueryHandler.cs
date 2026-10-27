using System.Text.Json;
using DeviceTelemetry.Application.Abstractions.Persistence;
using DeviceTelemetry.Application.Common;
using DeviceTelemetry.Application.Outbox;
using DeviceTelemetry.Application.Telemetry;
using DeviceTelemetry.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace DeviceTelemetry.Application.Mobile.Queries.GetDeviceEvents;

/// <summary>
/// Keyset pagination over (<c>OccurredAtUtc</c> DESC, <c>Id</c> DESC), served by the index
/// <c>IX_TelemetryEvents_DeviceId_OccurredAtUtc_Id</c>. Unlike OFFSET, page cost does not grow with depth,
/// and events with equal timestamps are neither skipped nor repeated because <c>Id</c> breaks ties. <c>notified</c> uses the
/// partial index of the same order; <c>excludeReports</c> filters along the main one. The email's delivery state comes from
/// its outbox message by two scalar subqueries (index on <c>TelemetryEventId</c>).
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

        if (query.Notified)
            events = events.Where(e => e.Notification == NotificationOutcome.Queued);

        if (query.ExcludeReports)
            events = events.Where(e => e.StatusCode != TelemetryStatusCodes.RegularReport);

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
                e.Notification,
                e.NotificationLanguage,
                e.NotificationSubject,
                OutboxStatus = context.OutboxMessages
                    .Where(m => m.TelemetryEventId == e.Id)
                    .OrderBy(m => m.Id)
                    .Select(m => (OutboxMessageStatus?)m.Status)
                    .FirstOrDefault(),
                SentAtUtc = context.OutboxMessages
                    .Where(m => m.TelemetryEventId == e.Id && m.Status == OutboxMessageStatus.Sent)
                    .OrderBy(m => m.Id)
                    .Select(m => m.ProcessedAtUtc)
                    .FirstOrDefault(),
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
                e.OccurredAtRejected,
                ToNotification(e.Notification, e.OutboxStatus, e.SentAtUtc, e.NotificationLanguage, e.NotificationSubject)))
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

    private static EventNotificationDto? ToNotification(
        NotificationOutcome? outcome, OutboxMessageStatus? delivery, DateTime? sentAtUtc, string? language, string? subject) =>
        outcome switch
        {
            null => null,
            NotificationOutcome.Queued => delivery switch
            {
                OutboxMessageStatus.Sent => new EventNotificationDto(EventNotificationDto.Sent, sentAtUtc, language, subject),
                OutboxMessageStatus.Failed => new EventNotificationDto(EventNotificationDto.Failed, null, language, subject),
                _ => new EventNotificationDto(EventNotificationDto.Queued, null, language, subject),
            },
            NotificationOutcome.Disabled => new EventNotificationDto(EventNotificationDto.Disabled, null, null, null),
            NotificationOutcome.NoTemplate => new EventNotificationDto(EventNotificationDto.NoTemplate, null, null, null),
            NotificationOutcome.NoAddress => new EventNotificationDto(EventNotificationDto.NoAddress, null, null, null),
            _ => new EventNotificationDto(EventNotificationDto.Failed, null, null, null),
        };
}
