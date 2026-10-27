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
/// <param name="ExcludeReports">Leave out regular reports (100010).</param>
/// <param name="Notified">Only events for which an email was queued (whatever its delivery state).</param>
public sealed record GetDeviceEventsQuery(
    int DeviceId,
    int Limit = GetDeviceEventsQuery.DefaultLimit,
    string? Cursor = null,
    bool ExcludeReports = false,
    bool Notified = false)
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
/// <param name="Notification">The email for this event; <c>null</c> for events stored before server 1.8.</param>
public sealed record DeviceEventDto(
    Guid Id,
    int StatusCode,
    DateTime OccurredAtUtc,
    DateTime ReceivedAtUtc,
    [property: JsonConverter(typeof(RawJsonConverter))] string? Payload,
    string Source,
    IReadOnlyList<TelemetryChannel>? Channels,
    bool OccurredAtRejected,
    EventNotificationDto? Notification);

/// <summary>What happened to the email for one event. No address is ever exposed.</summary>
/// <param name="Status">
/// <c>queued</c> (waiting to be sent or retried), <c>sent</c> (accepted by the mail service; delivery to the mailbox is not
/// known), <c>failed</c> (could not be sent), <c>disabled</c> (emails switched off on the device), <c>no_template</c> (this
/// code sends no email), <c>no_address</c> (no address on the device or the customer).
/// </param>
/// <param name="SentAtUtc">When the mail service accepted it; only for <c>sent</c>.</param>
/// <param name="Language">Language of the email (<c>en</c>, <c>ru</c>, <c>de</c>); <c>null</c> when none was queued.</param>
/// <param name="Subject">Subject of the email as sent; <c>null</c> when none was queued.</param>
public sealed record EventNotificationDto(string Status, DateTime? SentAtUtc, string? Language, string? Subject)
{
    public const string Queued = "queued";
    public const string Sent = "sent";
    public const string Failed = "failed";
    public const string Disabled = "disabled";
    public const string NoTemplate = "no_template";
    public const string NoAddress = "no_address";
}
