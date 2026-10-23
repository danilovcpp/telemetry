using DeviceTelemetry.Application.Common;
using DeviceTelemetry.Application.Telemetry;

namespace DeviceTelemetry.Application.Mobile.Queries.GetDeviceDetails;

/// <param name="DeviceId">Internal device id (<c>Device.Id</c>).</param>
public sealed record GetDeviceDetailsQuery(int DeviceId) : IQuery<DeviceDetailsDto>;

/// <summary>
/// Device card. <see cref="LastEventAtUtc"/> and <see cref="LastStatusCode"/> describe the most recent event by
/// <c>OccurredAtUtc</c> (the same order as the history): the last <em>known</em> status, not a guaranteed current state —
/// devices report on change and periodically, with no heartbeat contract. Both are <c>null</c> when there are no events.
/// <see cref="LastSeenAtUtc"/> is the server time of the device's last message; <see cref="SilentSinceUtc"/> is set while
/// the device is considered silent, which is only checked for devices that report <see cref="ReportIntervalS"/> or
/// <see cref="OfflineAfterS"/> and have <see cref="Monitoring"/> on. <see cref="TimeZone"/> and <see cref="Channels"/>
/// (the channel names) are set by the device. The notification address is deliberately not exposed (public API).
/// <see cref="Features"/> lists the optional screens the app may offer for the device (<c>live</c>, <c>history</c>); it is
/// always present and may be empty.
/// </summary>
public sealed record DeviceDetailsDto(
    int Id,
    string DeviceCode,
    string Name,
    int CustomerId,
    DateTime? LastEventAtUtc,
    int? LastStatusCode,
    DateTime? LastSeenAtUtc,
    int? ReportIntervalS,
    DateTime? SilentSinceUtc,
    int? OfflineAfterS,
    bool Monitoring,
    string? TimeZone,
    IReadOnlyList<TelemetryChannel>? Channels,
    IReadOnlyList<string> Features);
