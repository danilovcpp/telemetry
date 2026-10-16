using System.Text.Json.Serialization;
using DeviceTelemetry.Application.Common;

namespace DeviceTelemetry.Application.Mobile.Queries.GetDeviceEvents;

/// <summary>
/// One page of a device's telemetry history, newest first (by <c>OccurredAtUtc</c>, then <c>Id</c>).
/// </summary>
/// <param name="DeviceId">Internal device id (<c>Device.Id</c>), not the device code.</param>
/// <param name="Limit">Page size, 1–<see cref="MaxLimit"/>.</param>
/// <param name="Cursor">The <c>nextCursor</c> of the previous page; <c>null</c> for the first page.</param>
public sealed record GetDeviceEventsQuery(int DeviceId, int Limit = GetDeviceEventsQuery.DefaultLimit, string? Cursor = null)
    : IQuery<PagedResult<DeviceEventDto>>
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 100;
}

/// <param name="Id">Event id (time-ordered GUID).</param>
/// <param name="StatusCode">Status code reported by the device.</param>
/// <param name="OccurredAtUtc">When the event happened according to the device (server receive time if the device sent none).</param>
/// <param name="ReceivedAtUtc">When the server accepted the event.</param>
/// <param name="Payload">Device-supplied JSON object, emitted as JSON (not as a string); <c>null</c> if none was sent.</param>
public sealed record DeviceEventDto(
    Guid Id,
    int StatusCode,
    DateTime OccurredAtUtc,
    DateTime ReceivedAtUtc,
    [property: JsonConverter(typeof(RawJsonConverter))] string? Payload);
