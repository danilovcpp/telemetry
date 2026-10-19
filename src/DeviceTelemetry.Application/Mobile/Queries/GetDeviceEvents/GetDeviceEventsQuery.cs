using System.Text.Json.Serialization;
using DeviceTelemetry.Application.Common;
using DeviceTelemetry.Application.Telemetry;

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
/// <param name="Source"><c>device</c>, or <c>server</c> for events the server raised itself (190001, 190002).</param>
/// <param name="Channels">Channels the event refers to (e.g. 100003/100004); <c>null</c> if the device sent none.</param>
/// <param name="OccurredAtRejected">
/// The device's time was implausible (in the future) and <paramref name="OccurredAtUtc"/> is the server receive time instead.
/// </param>
public sealed record DeviceEventDto(
    Guid Id,
    int StatusCode,
    DateTime OccurredAtUtc,
    DateTime ReceivedAtUtc,
    [property: JsonConverter(typeof(RawJsonConverter))] string? Payload,
    string Source,
    IReadOnlyList<TelemetryChannel>? Channels,
    bool OccurredAtRejected);
