using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeviceTelemetry.Domain.Entities;
using DeviceTelemetry.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DeviceTelemetry.Infrastructure.DeviceLink;

/// <summary>
/// Copies the data files of a connected device to the server, one <c>file.read</c> at a time.
/// <list type="bullet">
/// <item>On connect and every <see cref="SyncOptions.IntervalS"/>: <c>logs.months</c> and <c>logs.days</c> per channel
/// (0 and every channel of the <c>hello</c>). The first sync of a device lists every month; later ones the current and
/// previous month plus months the server has not seen.</item>
/// <item>Missing bytes are read from the end of the copy (<c>offset</c> = copied size), newest files first, with
/// <see cref="SyncOptions.BackfillPauseMs"/> between reads (longer while live values stream).</item>
/// <item>A file a viewer asks for (<see cref="DeviceLinkSession.RequestFileSync"/>) is read next, without pauses.</item>
/// <item>A file that became shorter on the device is discarded and read again; a file missing from the listing keeps its
/// copy but is marked deleted.</item>
/// </list>
/// The device may return fewer bytes than requested (firmware returns at most 4 KB); reading continues at the returned end.
/// File contents are never logged.
/// </summary>
public sealed class LogSynchronizer(
    IServiceScopeFactory scopeFactory,
    LogFileStorage storage,
    LiveHub liveHub,
    IOptions<SyncOptions> options,
    TimeProvider timeProvider,
    ILogger<LogSynchronizer> logger)
{
    private readonly SyncOptions _options = options.Value;

    public async Task RunAsync(DeviceLinkSession session, CancellationToken ct)
    {
        if (!session.Hello.Supports(LiveHub.FilesCapability))
            return;

        var state = new SyncState(session);
        var nextListing = DateTimeOffset.MinValue;

        try
        {
            state.FirstSync = !await WithContextAsync(db => db.DeviceLogFiles.AnyAsync(f => f.DeviceId == session.DeviceId, ct));

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (session.OnDemandFiles.TryRead(out var requested))
                    {
                        await ReadOnDemandAsync(state, requested, ct);
                        continue;
                    }

                    if (timeProvider.GetUtcNow() >= nextListing)
                    {
                        await ListAsync(state, ct);
                        state.FirstSync = false;
                        state.Backlog = await LoadBacklogAsync(state, ct);
                        nextListing = timeProvider.GetUtcNow().AddSeconds(_options.IntervalS);
                        continue;
                    }

                    if (state.Backlog.TryPeek(out var fileId))
                    {
                        if (await ReadChunkAsync(state, fileId, ct) is not ChunkOutcome.More)
                            state.Backlog.Dequeue();

                        var pause = liveHub.IsStreaming(session.DeviceId) ? _options.LivePauseMs : _options.BackfillPauseMs;
                        await WaitAsync(session, TimeSpan.FromMilliseconds(pause), ct);
                        continue;
                    }

                    await WaitAsync(session, nextListing - timeProvider.GetUtcNow(), ct);
                }
                catch (DeviceLinkErrorException ex) when (ex.Code is "busy" or "card_error")
                {
                    var delay = TimeSpan.FromSeconds(Math.Clamp(ex.RetryAfterS ?? _options.RetryAfterDefaultS, 1, 3600));
                    logger.LogInformation("Device {DeviceId}: file sync paused for {Delay} ({Code})", session.DeviceId, delay, ex.Code);
                    await Task.Delay(delay, timeProvider, ct);
                }
                catch (DeviceLinkErrorException ex) when (ex.Code == "not_supported")
                {
                    logger.LogWarning("Device {DeviceId}: file requests not supported, file sync stopped", session.DeviceId);
                    return;
                }
                catch (TimeoutException)
                {
                    logger.LogWarning("Device {DeviceId}: no answer to a file request, retrying in the next round", session.DeviceId);
                    state.Backlog.Clear();
                }
                catch (DeviceLinkErrorException ex)
                {
                    logger.LogWarning("Device {DeviceId}: listing failed with '{Code}', retrying in the next round", session.DeviceId, ex.Code);
                    state.Backlog.Clear();
                }
                catch (DbUpdateException ex)
                {
                    logger.LogWarning(ex, "Device {DeviceId}: file metadata could not be saved, retrying in the next round", session.DeviceId);
                    state.Backlog.Clear();
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Connection closed.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Device {DeviceId}: file sync failed", session.DeviceId);
        }
    }

    /// <summary>Waits for <paramref name="delay"/> or until a viewer asks for a file.</summary>
    private static async Task WaitAsync(DeviceLinkSession session, TimeSpan delay, CancellationToken ct)
    {
        if (delay <= TimeSpan.Zero)
            return;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(delay);
        try
        {
            await session.OnDemandFiles.WaitToReadAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
        }
    }

    private async Task ListAsync(SyncState state, CancellationToken ct)
    {
        var session = state.Session;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var previousMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-1)
            .ToString("yyyy-MM", CultureInfo.InvariantCulture);

        foreach (var channel in session.Hello.Channels.Prepend(0).Distinct())
        {
            var answer = await RequestAsync(session, new JsonObject { ["op"] = "logs.months", ["ch"] = channel }, ct);
            var months = ReadStrings(answer, "months").Where(LogPaths.IsValidMonth).Distinct().Order(StringComparer.Ordinal).ToList();

            var knownMonths = await WithContextAsync(db => db.DeviceLogFiles
                .Where(f => f.DeviceId == session.DeviceId && f.Channel == channel)
                .Select(f => f.Month)
                .Distinct()
                .ToListAsync(ct));

            foreach (var month in months)
            {
                if (state.FirstSync || string.CompareOrdinal(month, previousMonth) >= 0 || !knownMonths.Contains(month))
                    await ListDaysAsync(state, channel, month, ct);
            }

            // Months the device no longer lists: keep the copies, hide them.
            var gone = knownMonths.Except(months).ToList();
            if (gone.Count > 0)
            {
                var hidden = await WithContextAsync(db => db.DeviceLogFiles
                    .Where(f => f.DeviceId == session.DeviceId && f.Channel == channel && gone.Contains(f.Month) && !f.Deleted)
                    .ExecuteUpdateAsync(s => s.SetProperty(f => f.Deleted, true), ct));
                if (hidden > 0)
                    logger.LogInformation("Device {DeviceId}: {Count} file(s) of channel {Channel} no longer on the device", session.DeviceId, hidden, channel);
            }
        }
    }

    private async Task ListDaysAsync(SyncState state, int channel, string month, CancellationToken ct)
    {
        var session = state.Session;
        var answer = await RequestAsync(session, new JsonObject { ["op"] = "logs.days", ["ch"] = channel, ["month"] = month }, ct);

        var listed = new Dictionary<string, (long Size, bool Closed)>(StringComparer.Ordinal);
        if (answer.TryGetProperty("days", out var days) && days.ValueKind == JsonValueKind.Array)
        {
            foreach (var day in days.EnumerateArray())
            {
                if (day.ValueKind != JsonValueKind.Object
                    || !day.TryGetProperty("path", out var p) || p.ValueKind != JsonValueKind.String
                    || !day.TryGetProperty("size", out var s) || !s.TryGetInt64(out var size) || size < 0)
                {
                    continue;
                }

                var path = p.GetString()!;
                if (!LogPaths.IsValid(path))
                {
                    logger.LogWarning("Device {DeviceId}: file with an invalid path ignored", session.DeviceId);
                    continue;
                }

                // "complete" (sent by some firmware) is ignored: the server decides completeness from closed and the copied size.
                var closed = day.TryGetProperty("closed", out var c) && c.ValueKind == JsonValueKind.True;
                listed[path] = (size, closed);
            }
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var paths = listed.Keys.ToList();
        var rows = await db.DeviceLogFiles
            .Where(f => f.DeviceId == session.DeviceId && ((f.Channel == channel && f.Month == month) || paths.Contains(f.Path)))
            .ToListAsync(ct);
        var byPath = rows.ToDictionary(f => f.Path, StringComparer.Ordinal);
        var discarded = new List<string>();

        foreach (var (path, (size, closed)) in listed)
        {
            if (!byPath.TryGetValue(path, out var row))
            {
                db.DeviceLogFiles.Add(new DeviceLogFile
                {
                    DeviceId = session.DeviceId,
                    Channel = channel,
                    Month = month,
                    Path = path,
                    DeviceSize = size,
                    Closed = closed,
                    SyncedAtUtc = size == 0 ? timeProvider.GetUtcNow().UtcDateTime : null,
                });
                continue;
            }

            if (size < row.StoredSize)
            {
                logger.LogWarning(
                    "Device {DeviceId}: file {FileId} shrank on the device from {Stored} to {Size} bytes; copy discarded and read again",
                    session.DeviceId, row.Id, row.StoredSize, size);
                row.StoredSize = 0;
                row.Generation++;
                discarded.Add(path);
            }

            row.Channel = channel;
            row.Month = month;
            row.DeviceSize = size;
            row.Closed = closed;
            row.Deleted = false;
        }

        foreach (var row in rows.Where(r => r.Channel == channel && r.Month == month && !listed.ContainsKey(r.Path) && !r.Deleted))
        {
            logger.LogInformation("Device {DeviceId}: file {FileId} no longer on the device; copy kept", session.DeviceId, row.Id);
            row.Deleted = true;
        }

        await db.SaveChangesAsync(ct);

        // Metadata first: a reader never gets more bytes than the row says are copied.
        foreach (var path in discarded)
            storage.Delete(session.DeviceId, path);
    }

    /// <summary>Files with missing bytes, newest month and day first.</summary>
    private async Task<Queue<long>> LoadBacklogAsync(SyncState state, CancellationToken ct)
    {
        var ids = await WithContextAsync(db => db.DeviceLogFiles
            .Where(f => f.DeviceId == state.Session.DeviceId && !f.Deleted && f.StoredSize < f.DeviceSize)
            .OrderByDescending(f => f.Month)
            .ThenByDescending(f => f.Path)
            .Select(f => f.Id)
            .ToListAsync(ct));

        return new Queue<long>(ids.Where(id => !state.Skipped.Contains(id)));
    }

    private async Task ReadOnDemandAsync(SyncState state, string path, CancellationToken ct)
    {
        var fileId = await WithContextAsync(db => db.DeviceLogFiles
            .Where(f => f.DeviceId == state.Session.DeviceId && f.Path == path && !f.Deleted)
            .Select(f => (long?)f.Id)
            .SingleOrDefaultAsync(ct));

        if (fileId is null || state.Skipped.Contains(fileId.Value))
            return;

        logger.LogInformation("Device {DeviceId}: reading file {FileId} for a viewer", state.Session.DeviceId, fileId);
        while (await ReadChunkAsync(state, fileId.Value, ct) is ChunkOutcome.More)
        {
        }
    }

    private enum ChunkOutcome
    {
        More,
        Done,
        Skipped,
    }

    /// <summary>One <c>file.read</c> from the end of the copy; appends what the device returned.</summary>
    private async Task<ChunkOutcome> ReadChunkAsync(SyncState state, long fileId, CancellationToken ct)
    {
        var session = state.Session;
        var file = await WithContextAsync(db => db.DeviceLogFiles
            .AsNoTracking()
            .Where(f => f.Id == fileId)
            .Select(f => new { f.Path, f.StoredSize, f.DeviceSize, f.Generation, f.Deleted })
            .SingleOrDefaultAsync(ct));

        if (file is null || file.Deleted)
            return ChunkOutcome.Skipped;

        JsonElement answer;
        try
        {
            answer = await RequestAsync(session, new JsonObject
            {
                ["op"] = "file.read",
                ["path"] = file.Path,
                ["offset"] = file.StoredSize,
                ["length"] = _options.ReadLength,
            }, ct);
        }
        catch (DeviceLinkErrorException ex) when (ex.Code == "not_found")
        {
            logger.LogInformation("Device {DeviceId}: file {FileId} not found on the device; copy kept", session.DeviceId, fileId);
            await WithContextAsync(db => db.DeviceLogFiles.Where(f => f.Id == fileId)
                .ExecuteUpdateAsync(s => s.SetProperty(f => f.Deleted, true), ct));
            return ChunkOutcome.Skipped;
        }
        catch (DeviceLinkErrorException ex) when (ex.Code is "bad_path" or "bad_request")
        {
            logger.LogWarning("Device {DeviceId}: file {FileId} refused with '{Code}'; skipped on this connection", session.DeviceId, fileId, ex.Code);
            state.Skipped.Add(fileId);
            return ChunkOutcome.Skipped;
        }

        if (!answer.TryGetProperty("offset", out var o) || !o.TryGetInt64(out var offset) || offset != file.StoredSize
            || !answer.TryGetProperty("data", out var d) || d.ValueKind != JsonValueKind.String
            || !d.TryGetBytesFromBase64(out var data))
        {
            logger.LogWarning("Device {DeviceId}: malformed file.data for file {FileId}; skipped on this connection", session.DeviceId, fileId);
            state.Skipped.Add(fileId);
            return ChunkOutcome.Skipped;
        }

        var deviceSize = answer.TryGetProperty("size", out var s) && s.TryGetInt64(out var size) ? size : file.DeviceSize;
        var eof = answer.TryGetProperty("eof", out var e) && e.ValueKind == JsonValueKind.True;

        if (deviceSize < offset)
        {
            // Shrank since the listing: discard; the next listing round re-reads it from the start.
            logger.LogWarning("Device {DeviceId}: file {FileId} shrank while copying; copy discarded and read again", session.DeviceId, fileId);
            await WithContextAsync(db => db.DeviceLogFiles.Where(f => f.Id == fileId && f.Generation == file.Generation)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(f => f.StoredSize, 0L)
                    .SetProperty(f => f.DeviceSize, deviceSize)
                    .SetProperty(f => f.Generation, f => f.Generation + 1), ct));
            storage.Delete(session.DeviceId, file.Path);
            return ChunkOutcome.More;
        }

        if (data.Length > 0)
            await storage.WriteAsync(session.DeviceId, file.Path, offset, data, ct);

        var stored = offset + data.Length;
        var newDeviceSize = Math.Max(deviceSize, stored);
        var now = timeProvider.GetUtcNow().UtcDateTime;

        // Conditional on the state that was read: a concurrent listing that discarded the copy wins.
        var updated = await WithContextAsync(db => db.DeviceLogFiles
            .Where(f => f.Id == fileId && f.Generation == file.Generation && f.StoredSize == offset)
            .ExecuteUpdateAsync(u => u
                .SetProperty(f => f.StoredSize, stored)
                .SetProperty(f => f.DeviceSize, newDeviceSize)
                .SetProperty(f => f.SyncedAtUtc, now), ct));

        if (updated == 0)
            return ChunkOutcome.Skipped;

        return eof || data.Length == 0 || stored >= newDeviceSize ? ChunkOutcome.Done : ChunkOutcome.More;
    }

    private Task<JsonElement> RequestAsync(DeviceLinkSession session, JsonObject request, CancellationToken ct) =>
        session.RequestAsync(request, _options.RequestTimeout, ct);

    private async Task<T> WithContextAsync<T>(Func<ApplicationDbContext, Task<T>> action)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>());
    }

    private static IEnumerable<string> ReadStrings(JsonElement answer, string property) =>
        answer.TryGetProperty(property, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList()
            : [];

    private sealed class SyncState(DeviceLinkSession session)
    {
        public DeviceLinkSession Session { get; } = session;
        public bool FirstSync { get; set; }
        public Queue<long> Backlog { get; set; } = new();

        /// <summary>Files the device refused on this connection.</summary>
        public HashSet<long> Skipped { get; } = [];
    }
}
