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
public sealed record DeviceListItemDto(int Id, string DeviceCode, string Name);
