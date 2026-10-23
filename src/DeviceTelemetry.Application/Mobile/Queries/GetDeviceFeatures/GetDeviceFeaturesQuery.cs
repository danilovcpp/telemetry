using DeviceTelemetry.Application.Abstractions.Persistence;
using DeviceTelemetry.Application.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DeviceTelemetry.Application.Mobile.Queries.GetDeviceFeatures;

/// <summary>The <c>features</c> of one device, as in the device card; used to gate the live and stored-data routes.</summary>
public sealed record GetDeviceFeaturesQuery(int DeviceId) : IQuery<IReadOnlyList<string>>;

public sealed class GetDeviceFeaturesQueryHandler(IApplicationDbContext context, IOptions<MobileFeatureOptions> options)
    : IQueryHandler<GetDeviceFeaturesQuery, IReadOnlyList<string>>
{
    public async Task<Result<IReadOnlyList<string>>> HandleAsync(GetDeviceFeaturesQuery query, CancellationToken ct)
    {
        var device = await context.Devices
            .AsNoTracking()
            .Where(d => d.Id == query.DeviceId)
            .Select(d => new { d.LinkHello, HasCopies = d.LogFiles.Any(f => f.StoredSize > 0 && !f.Deleted) })
            .SingleOrDefaultAsync(ct);

        if (device is null)
            return MobileErrors.DeviceNotFound;

        return Result<IReadOnlyList<string>>.Success(MobileFeatures.For(options.Value, device.LinkHello, device.HasCopies));
    }
}
