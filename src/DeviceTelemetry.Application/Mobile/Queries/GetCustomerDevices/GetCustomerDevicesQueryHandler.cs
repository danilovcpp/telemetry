using DeviceTelemetry.Application.Abstractions.Persistence;
using DeviceTelemetry.Application.Common;
using Microsoft.EntityFrameworkCore;

namespace DeviceTelemetry.Application.Mobile.Queries.GetCustomerDevices;

public sealed class GetCustomerDevicesQueryHandler(IApplicationDbContext context)
    : IQueryHandler<GetCustomerDevicesQuery, IReadOnlyList<DeviceListItemDto>>
{
    public async Task<Result<IReadOnlyList<DeviceListItemDto>>> HandleAsync(GetCustomerDevicesQuery query, CancellationToken ct)
    {
        // One SQL statement: the customer row (to tell "unknown customer" from "no devices") joined with its devices.
        var customer = await context.Customers
            .AsNoTracking()
            .Where(c => c.Id == query.CustomerId)
            .Select(c => new
            {
                Devices = c.Devices
                    .OrderBy(d => d.Name)
                    .ThenBy(d => d.Id)
                    .Select(d => new DeviceListItemDto(d.Id, d.DeviceCode, d.Name))
                    .ToList(),
            })
            .SingleOrDefaultAsync(ct);

        if (customer is null)
            return MobileErrors.CustomerNotFound;

        return customer.Devices;
    }
}
