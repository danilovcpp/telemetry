using System.Text.Json.Serialization;
using DeviceTelemetry.Application.Common;

namespace DeviceTelemetry.Application.Mobile.Queries.GetDeviceState;

/// <param name="DeviceId">Internal device id (<c>Device.Id</c>).</param>
public sealed record GetDeviceStateQuery(int DeviceId) : IQuery<DeviceStateDto>;

/// <summary>
/// Current state of a device: its last regular report (status 100010) plus the server's liveness view.
/// </summary>
/// <param name="DeviceId">Internal device id.</param>
/// <param name="LastSeenAtUtc">Server time of the device's last message of any kind; <c>null</c> if it never reported.</param>
/// <param name="ReportIntervalS">Report period the device announced; <c>null</c> when it does not report one (then it is not watched for silence).</param>
/// <param name="SilentSinceUtc">When the server declared the device silent (190001); <c>null</c> while it reports.</param>
/// <param name="LastReport">The latest 100010 report by device time; <c>null</c> if there is none.</param>
public sealed record DeviceStateDto(
    int DeviceId,
    DateTime? LastSeenAtUtc,
    int? ReportIntervalS,
    DateTime? SilentSinceUtc,
    DeviceReportDto? LastReport);

/// <param name="EventId">Id of the 100010 event.</param>
/// <param name="OccurredAtUtc">Report time by the device clock (receive time if the device sent none or an implausible one).</param>
/// <param name="ReceivedAtUtc">When the server accepted the report.</param>
/// <param name="Payload">The report as sent; values and the state of each channel are in <c>payload.values</c>.</param>
public sealed record DeviceReportDto(
    Guid EventId,
    DateTime OccurredAtUtc,
    DateTime ReceivedAtUtc,
    [property: JsonConverter(typeof(RawJsonConverter))] string? Payload);
