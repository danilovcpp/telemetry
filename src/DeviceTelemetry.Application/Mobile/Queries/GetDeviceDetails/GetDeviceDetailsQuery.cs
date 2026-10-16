using DeviceTelemetry.Application.Common;

namespace DeviceTelemetry.Application.Mobile.Queries.GetDeviceDetails;

/// <param name="DeviceId">Internal device id (<c>Device.Id</c>).</param>
public sealed record GetDeviceDetailsQuery(int DeviceId) : IQuery<DeviceDetailsDto>;

/// <summary>
/// Device card. <see cref="LastEventAtUtc"/> and <see cref="LastStatusCode"/> describe the most recent event by
/// <c>OccurredAtUtc</c> (the same order as the history): the last <em>known</em> status, not a guaranteed current state —
/// devices report on change and periodically, with no heartbeat contract. Both are <c>null</c> when there are no events.
/// </summary>
public sealed record DeviceDetailsDto(
    int Id,
    string DeviceCode,
    string Name,
    int CustomerId,
    DateTime? LastEventAtUtc,
    int? LastStatusCode);
