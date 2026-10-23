using System.Text.RegularExpressions;
using DeviceTelemetry.Application.Abstractions.DeviceLink;
using DeviceTelemetry.Application.Abstractions.Persistence;
using DeviceTelemetry.Application.Common;
using DeviceTelemetry.Application.Mobile.Queries.GetDeviceFeatures;
using Microsoft.EntityFrameworkCore;

namespace DeviceTelemetry.Application.Mobile.Queries.GetDeviceLogs;

/// <summary>Months with data files of one channel (0 = summary file), ascending.</summary>
public sealed record GetDeviceLogMonthsQuery(int DeviceId, int Channel) : IQuery<DeviceLogMonthsDto>;

public sealed record DeviceLogMonthsDto(IReadOnlyList<string> Months);

/// <summary>Daily files of one channel in one month (<c>YYYY-MM</c>), ascending.</summary>
public sealed record GetDeviceLogDaysQuery(int DeviceId, int Channel, string Month) : IQuery<DeviceLogDaysDto>;

public sealed record DeviceLogDaysDto(IReadOnlyList<DeviceLogDayDto> Days);

/// <param name="Path">Device path of the file; pass it URL-encoded to <c>/data/{path}</c>.</param>
/// <param name="Size">Bytes available on the server now.</param>
/// <param name="Complete">The day is closed and the copy has every byte: the file will not change any more.</param>
public sealed record DeviceLogDayDto(string Path, long Size, bool Complete);

/// <summary>Access to the server copy of one data file.</summary>
public sealed record GetDeviceLogFileQuery(int DeviceId, string Path) : IQuery<LogFileAccess>;

public enum LogFileAvailability
{
    /// <summary>A copy exists and is served, whether or not the device is online.</summary>
    Available,

    /// <summary>No copy yet; the device is online and the file was queued for copying.</summary>
    Pending,

    /// <summary>No copy yet and the device is not connected.</summary>
    Offline,
}

/// <param name="StoredSize">Bytes of the copy to serve.</param>
/// <param name="ETag">Changes whenever the copy grows or is re-read.</param>
/// <param name="RetryAfterS">For <see cref="LogFileAvailability.Pending"/>: estimated seconds until the copy exists.</param>
public sealed record LogFileAccess(
    LogFileAvailability Availability, string Path, long StoredSize, string ETag, DateTime? LastModifiedUtc, int? RetryAfterS);

public sealed partial class DeviceLogsQueryHandler(
    IApplicationDbContext context,
    IDeviceLinkStatus linkStatus,
    IQueryHandler<GetDeviceFeaturesQuery, IReadOnlyList<string>> featuresHandler)
    : IQueryHandler<GetDeviceLogMonthsQuery, DeviceLogMonthsDto>,
      IQueryHandler<GetDeviceLogDaysQuery, DeviceLogDaysDto>,
      IQueryHandler<GetDeviceLogFileQuery, LogFileAccess>
{
    /// <summary>Copy throughput of the device link, used to estimate Retry-After.</summary>
    private const int BytesPerSecond = 16 * 1024;

    [GeneratedRegex(@"^\d{4}-(0[1-9]|1[0-2])$")]
    private static partial Regex MonthPattern();

    public async Task<Result<DeviceLogMonthsDto>> HandleAsync(GetDeviceLogMonthsQuery query, CancellationToken ct)
    {
        if (await CheckHistoryAsync(query.DeviceId, ct) is { } error)
            return error;

        var months = await context.DeviceLogFiles
            .AsNoTracking()
            .Where(f => f.DeviceId == query.DeviceId && f.Channel == query.Channel && !f.Deleted)
            .Select(f => f.Month)
            .Distinct()
            .OrderBy(m => m)
            .ToListAsync(ct);

        return new DeviceLogMonthsDto(months);
    }

    public async Task<Result<DeviceLogDaysDto>> HandleAsync(GetDeviceLogDaysQuery query, CancellationToken ct)
    {
        if (await CheckHistoryAsync(query.DeviceId, ct) is { } error)
            return error;

        if (!MonthPattern().IsMatch(query.Month))
            return MobileErrors.InvalidMonth;

        var files = await context.DeviceLogFiles
            .AsNoTracking()
            .Where(f => f.DeviceId == query.DeviceId && f.Channel == query.Channel && f.Month == query.Month && !f.Deleted)
            .Select(f => new { f.Path, f.StoredSize, f.DeviceSize, f.Closed })
            .ToListAsync(ct);

        if (files.Count == 0)
            return MobileErrors.LogNotFound;

        return new DeviceLogDaysDto(files
            .OrderBy(f => f.Path, StringComparer.Ordinal)
            .Select(f => new DeviceLogDayDto(f.Path, f.StoredSize, f.Closed && f.StoredSize == f.DeviceSize))
            .ToList());
    }

    public async Task<Result<LogFileAccess>> HandleAsync(GetDeviceLogFileQuery query, CancellationToken ct)
    {
        if (await CheckHistoryAsync(query.DeviceId, ct) is { } error)
            return error;

        // The path is only ever compared with paths the device reported; it is never used to build a disk path here.
        var file = await context.DeviceLogFiles
            .AsNoTracking()
            .Where(f => f.DeviceId == query.DeviceId && f.Path == query.Path && !f.Deleted)
            .Select(f => new { f.Path, f.StoredSize, f.DeviceSize, f.Generation, f.SyncedAtUtc })
            .SingleOrDefaultAsync(ct);

        if (file is null)
            return MobileErrors.LogNotFound;

        var etag = $"\"{file.Generation:x}-{file.StoredSize:x}\"";

        // An empty file on the device is complete as soon as it is listed: there is nothing to copy.
        if (file.StoredSize > 0 || file.DeviceSize == 0)
            return new LogFileAccess(LogFileAvailability.Available, file.Path, file.StoredSize, etag, file.SyncedAtUtc, null);

        if (!linkStatus.IsConnected(query.DeviceId))
            return new LogFileAccess(LogFileAvailability.Offline, file.Path, 0, etag, null, null);

        linkStatus.RequestFileSync(query.DeviceId, file.Path);
        var retryAfter = (int)Math.Clamp(file.DeviceSize / BytesPerSecond + 5, 5, 600);
        return new LogFileAccess(LogFileAvailability.Pending, file.Path, 0, etag, null, retryAfter);
    }

    private async Task<DomainError?> CheckHistoryAsync(int deviceId, CancellationToken ct)
    {
        var features = await featuresHandler.HandleAsync(new GetDeviceFeaturesQuery(deviceId), ct);
        if (features.IsFailure)
            return features.Error;

        return features.Value.Contains(MobileFeatures.History) ? null : MobileErrors.FeatureNotAvailable;
    }
}
