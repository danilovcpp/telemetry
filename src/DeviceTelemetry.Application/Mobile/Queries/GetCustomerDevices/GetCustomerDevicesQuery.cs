using DeviceTelemetry.Application.Common;

namespace DeviceTelemetry.Application.Mobile.Queries.GetCustomerDevices;

/// <summary>
/// All devices of a customer, sorted by name and then id. Not paginated: devices are provisioned manually,
/// so a customer has a small, bounded number of them (unlike telemetry events).
/// </summary>
public sealed record GetCustomerDevicesQuery(int CustomerId) : IQuery<IReadOnlyList<DeviceListItemDto>>;

/// <param name="Id">Internal device id, used by the other mobile endpoints.</param>
/// <param name="DeviceCode">External identifier the device reports telemetry with.</param>
/// <param name="Name">Display name.</param>
/// <param name="LastSeenAtUtc">
/// Server time of the device's last message: its age answers "is the device alive?" independently of the device clock.
/// <c>null</c> if the device never reported.
/// </param>
/// <param name="SilentSinceUtc">Set while the server considers the device silent (status 190001 raised); otherwise <c>null</c>.</param>
public sealed record DeviceListItemDto(int Id, string DeviceCode, string Name, DateTime? LastSeenAtUtc, DateTime? SilentSinceUtc);
