using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeviceTelemetry.Domain.Entities;
using DeviceTelemetry.Infrastructure.DeviceLink;
using DeviceTelemetry.Tests.Shared;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DeviceTelemetry.IntegrationTests.DeviceLink;

/// <summary>
/// Device Link v1 and the mobile live / stored-data routes end to end: the real API on a test server, a fake device over
/// WebSocket, a real database (checklist 6.3 of the SG Manager task).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DeviceLinkTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private readonly List<DeviceLinkFactory> _factories = [];
    private string _connectionString = string.Empty;

    private static string CurrentMonth => DateTime.UtcNow.ToString("yyyy-MM", CultureInfo.InvariantCulture);
    private static string MonthsAgo(int n) => DateTime.UtcNow.AddMonths(-n).ToString("yyyy-MM", CultureInfo.InvariantCulture);

    public async Task InitializeAsync() => _connectionString = await postgres.CreateEmptyDatabaseAsync();

    public async Task DisposeAsync()
    {
        foreach (var factory in _factories)
            await factory.DisposeAsync();

        await using var db = PostgresFixture.CreateContext(_connectionString);
        await db.Database.EnsureDeletedAsync();
    }

    private TestServer StartServer(Dictionary<string, string?>? settings = null)
    {
        var factory = new DeviceLinkFactory(_connectionString, settings);
        _factories.Add(factory);
        return factory.Server;
    }

    private async Task<Device> SeedDeviceAsync(Action<Device>? configure = null)
    {
        await using var db = PostgresFixture.CreateContext(_connectionString);
        var device = await TestData.SeedDeviceAsync(db);
        if (configure is not null)
        {
            configure(device);
            await db.SaveChangesAsync();
        }

        return device;
    }

    private async Task<List<DeviceLogFile>> FilesAsync(int deviceId)
    {
        await using var db = PostgresFixture.CreateContext(_connectionString);
        return await db.DeviceLogFiles.AsNoTracking().Where(f => f.DeviceId == deviceId).OrderBy(f => f.Path).ToListAsync();
    }

    private static async Task EventuallyAsync(Func<Task<bool>> condition, string because)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail($"Timed out waiting until {because}.");
            await Task.Delay(50);
        }
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await JsonAsync(response)).TryGetProperty("code", out var code) ? code.GetString() : null;

    private static byte[] TsvBytes(int lines, string seed = "")
    {
        var text = new StringBuilder("# SG Manager\tch1\r\n");
        for (var i = 0; i < lines; i++)
            text.Append(CultureInfo.InvariantCulture, $"2026-10-01 00:{i % 60:00}:00\t{i},5{seed}\t-99999,50\r\n");
        return Encoding.ASCII.GetBytes(text.ToString());
    }

    private static string DataUrl(int deviceId, string path) => $"/api/mobile/devices/{deviceId}/data/{Uri.EscapeDataString(path)}";

    // --- Link handshake ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Wrong_key_is_refused_with_401_before_the_upgrade()
    {
        var server = StartServer();
        var device = await SeedDeviceAsync();

        var error = await Assert.ThrowsAnyAsync<Exception>(() => FakeDevice.OpenAsync(server, device.DeviceCode, "dtk_wrong"));
        Assert.Contains("401", error.Message);

        error = await Assert.ThrowsAnyAsync<Exception>(() => FakeDevice.OpenAsync(server, "no-such-device", TestData.DeviceKey));
        Assert.Contains("401", error.Message);
    }

    [Fact]
    public async Task Another_link_version_is_closed_with_4400()
    {
        var server = StartServer();
        var device = await SeedDeviceAsync();
        using var socket = await FakeDevice.OpenAsync(server, device.DeviceCode, TestData.DeviceKey);

        var hello = FakeDevice.Hello("live");
        hello["link"] = 2;
        await socket.SendAsync(Encoding.UTF8.GetBytes(hello.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None);

        using var timeout = new CancellationTokenSource(Patience);
        var result = await socket.ReceiveAsync(new byte[1024], timeout.Token);
        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        Assert.Equal(4400, (int)result.CloseStatus!.Value);
    }

    [Fact]
    public async Task A_new_connection_replaces_the_old_one_with_4409()
    {
        var server = StartServer();
        var device = await SeedDeviceAsync();

        await using var first = await FakeDevice.ConnectAsync(server, device.DeviceCode, TestData.DeviceKey);
        await using var second = await FakeDevice.ConnectAsync(server, device.DeviceCode, TestData.DeviceKey);

        await first.Finished.WaitAsync(Patience);
        Assert.Equal(4409, (int)first.CloseStatus!.Value);

        var registry = server.Services.GetRequiredService<DeviceLinkRegistry>();
        await EventuallyAsync(() => Task.FromResult(registry.IsConnected(device.Id)), "the second connection is registered");
    }

    // --- Features -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Features_follow_the_capabilities_of_the_last_hello()
    {
        var server = StartServer();
        var client = server.CreateClient();
        var device = await SeedDeviceAsync();

        var card = await client.GetFromJsonAsync<JsonElement>($"/api/mobile/devices/{device.Id}");
        Assert.Equal(0, card.GetProperty("features").GetArrayLength());

        await using (await FakeDevice.ConnectAsync(server, device.DeviceCode, TestData.DeviceKey, FakeDevice.Hello("live", "files")))
        {
            card = await client.GetFromJsonAsync<JsonElement>($"/api/mobile/devices/{device.Id}");
            Assert.Equal(["live", "history"], card.GetProperty("features").EnumerateArray().Select(f => f.GetString()));
        }

        // The stored hello decides, also while the device is offline; a firmware without live loses the feature.
        await using (await FakeDevice.ConnectAsync(server, device.DeviceCode, TestData.DeviceKey, FakeDevice.Hello("files")))
        {
        }

        card = await client.GetFromJsonAsync<JsonElement>($"/api/mobile/devices/{device.Id}");
        Assert.Equal(["history"], card.GetProperty("features").EnumerateArray().Select(f => f.GetString()));
    }

    [Fact]
    public async Task Production_configuration_hides_the_features_and_refuses_the_routes()
    {
        var server = StartServer(new Dictionary<string, string?>
        {
            ["DeviceLink:Enabled"] = "false",
            ["MobileApi:LiveEnabled"] = "false",
            ["MobileApi:HistoryEnabled"] = "false",
        });
        var client = server.CreateClient();
        var device = await SeedDeviceAsync(d => d.LinkHello = FakeDevice.Hello("live", "files").ToJsonString());

        var card = await client.GetFromJsonAsync<JsonElement>($"/api/mobile/devices/{device.Id}");
        Assert.Equal(0, card.GetProperty("features").GetArrayLength());

        foreach (var url in new[]
                 {
                     $"/api/mobile/devices/{device.Id}/live",
                     $"/api/mobile/devices/{device.Id}/logs?ch=1",
                     DataUrl(device.Id, "LOG/CH1/01.TSV"),
                 })
        {
            var response = await client.GetAsync(url);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("feature.not_available", await CodeAsync(response));
        }

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/device/v1/link")).StatusCode);
    }

    [Fact]
    public async Task Live_of_an_unknown_device_is_404_before_the_upgrade()
    {
        var client = StartServer().CreateClient();

        var response = await client.GetAsync("/api/mobile/devices/2147483647/live");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("device.not_found", await CodeAsync(response));
    }

    // --- Live ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Two_viewers_share_one_device_stream_that_is_renewed_and_stopped_after_the_delay()
    {
        var server = StartServer();
        var device = await SeedDeviceAsync();
        await using var link = await FakeDevice.ConnectAsync(server, device.DeviceCode, TestData.DeviceKey);

        var first = await LiveViewer.ConnectAsync(server, device.Id);
        var second = await LiveViewer.ConnectAsync(server, device.Id);
        await first.ExpectStatusAsync(LiveHub.Connecting);
        await second.ExpectStatusAsync(LiveHub.Connecting);
        await EventuallyAsync(() => Task.FromResult(link.LiveStarts >= 1), "live.start reached the device");
        Assert.Equal(1, link.LiveStarts);

        await link.SendValuesAsync(1);
        foreach (var viewer in new[] { first, second })
        {
            await viewer.ExpectStatusAsync(LiveHub.Streaming);
            var values = await viewer.ExpectValuesAsync();
            Assert.Equal(3, values.GetProperty("schema").GetInt32());
            Assert.Equal(1, values.GetProperty("t").GetInt32());
            Assert.Equal("2026-10-23T10:00:00Z", values.GetProperty("atUtc").GetString());
            Assert.True(values.TryGetProperty("receivedAtUtc", out _));
            Assert.Equal(2, values.GetProperty("values").GetArrayLength());
        }

        // Lease renewal (every second in this configuration).
        await EventuallyAsync(() => Task.FromResult(link.LiveStarts >= 3), "live.start is renewed");

        await first.DisposeAsync();
        await Task.Delay(200);
        Assert.Equal(0, link.LiveStops);

        await second.DisposeAsync();
        await EventuallyAsync(() => Task.FromResult(link.LiveStops == 1), "live.stop is sent after the stop delay");

        var starts = link.LiveStarts;
        await Task.Delay(1500);
        Assert.Equal(starts, link.LiveStarts);
    }

    [Fact]
    public async Task Viewers_see_device_offline_and_the_stream_resumes_after_reconnect()
    {
        var server = StartServer();
        var device = await SeedDeviceAsync();
        var link = await FakeDevice.ConnectAsync(server, device.DeviceCode, TestData.DeviceKey);

        await using var viewer = await LiveViewer.ConnectAsync(server, device.Id);
        await viewer.ExpectStatusAsync(LiveHub.Connecting);
        await link.SendValuesAsync(1);
        await viewer.ExpectStatusAsync(LiveHub.Streaming);

        await link.DisposeAsync();
        await viewer.ExpectStatusAsync(LiveHub.DeviceOffline);

        await using var again = await FakeDevice.ConnectAsync(server, device.DeviceCode, TestData.DeviceKey);
        await viewer.ExpectStatusAsync(LiveHub.Connecting);
        await EventuallyAsync(() => Task.FromResult(again.LiveStarts >= 1), "live.start is sent on the new connection");

        await again.SendValuesAsync(2);
        await viewer.ExpectStatusAsync(LiveHub.Streaming);
        Assert.Equal(2, (await viewer.ExpectValuesAsync()).GetProperty("t").GetInt32());
    }

    [Fact]
    public async Task A_viewer_of_an_offline_device_gets_device_offline_first()
    {
        var server = StartServer();
        var device = await SeedDeviceAsync(d => d.LinkHello = FakeDevice.Hello("live").ToJsonString());

        await using var viewer = await LiveViewer.ConnectAsync(server, device.Id);

        await viewer.ExpectStatusAsync(LiveHub.DeviceOffline);
    }

    [Fact]
    public async Task Not_supported_ends_the_viewer_stream_with_1000()
    {
        var server = StartServer();
        var device = await SeedDeviceAsync();
        await using var link = await FakeDevice.ConnectAsync(server, device.DeviceCode, TestData.DeviceKey,
            setup: d => d.LiveStartError = "not_supported");

        await using var viewer = await LiveViewer.ConnectAsync(server, device.Id);
        await viewer.ExpectStatusAsync(LiveHub.NotSupported);

        Assert.Null(await viewer.NextAsync());
        Assert.Equal(WebSocketCloseStatus.NormalClosure, viewer.CloseStatus);
    }

    [Fact]
    public async Task Viewer_limit_per_address_is_429_before_the_upgrade()
    {
        var server = StartServer(new Dictionary<string, string?> { ["Live:MaxViewersPerIp"] = "1" });
        var device = await SeedDeviceAsync(d => d.LinkHello = FakeDevice.Hello("live").ToJsonString());

        await using var viewer = await LiveViewer.ConnectAsync(server, device.Id);
        await viewer.ExpectStatusAsync(LiveHub.DeviceOffline);

        var error = await Assert.ThrowsAnyAsync<Exception>(() => LiveViewer.ConnectAsync(server, device.Id));
        Assert.Contains("429", error.Message);
    }

    // --- Stored data --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task First_sync_copies_everything_in_4kb_answers_and_later_syncs_read_only_tails()
    {
        var server = StartServer(new Dictionary<string, string?> { ["Sync:IntervalS"] = "1" });
        var client = server.CreateClient();
        var device = await SeedDeviceAsync();

        var summary = TsvBytes(300);
        var old = TsvBytes(120);
        var today = TsvBytes(60);
        var oldMonth = MonthsAgo(4);
        await using var link = await FakeDevice.ConnectAsync(server, device.DeviceCode, TestData.DeviceKey, setup: d =>
        {
            d.AddFile(0, oldMonth, $"LOG/SUM/{oldMonth}/01.TSV", summary, closed: true);
            d.AddFile(1, oldMonth, $"LOG/CH1/{oldMonth}/01.TSV", old, closed: true);
            d.AddFile(1, CurrentMonth, $"LOG/CH1/{CurrentMonth}/01.TSV", today, closed: false);
        });

        await EventuallyAsync(async () => (await FilesAsync(device.Id)) is { Count: 3 } files && files.All(f => f.StoredSize == f.DeviceSize && f.DeviceSize > 0),
            "every file is copied");

        Assert.True(summary.Length > 2 * FakeDevice.MaxChunk);
        Assert.Equal([0L, 4096L, 8192L], link.Reads.Where(r => r.Path.StartsWith("LOG/SUM", StringComparison.Ordinal)).Select(r => r.Offset).Take(3));
        Assert.Equal(summary, await client.GetByteArrayAsync(DataUrl(device.Id, $"LOG/SUM/{oldMonth}/01.TSV")));
        Assert.Equal(old, await client.GetByteArrayAsync(DataUrl(device.Id, $"LOG/CH1/{oldMonth}/01.TSV")));

        // The open day grows: the next round reads only the new tail, and the old files are not read again.
        var grown = today.Concat(Encoding.ASCII.GetBytes("2026-10-01 01:00:00\t7,5\t-99999,50\r\n")).ToArray();
        var readsBefore = link.Reads.Count;
        link.AddFile(1, CurrentMonth, $"LOG/CH1/{CurrentMonth}/01.TSV", grown, closed: false);

        await EventuallyAsync(async () => (await FilesAsync(device.Id)).Single(f => f.Path.Contains(CurrentMonth)).StoredSize == grown.Length,
            "the tail is copied");

        var later = link.Reads.Skip(readsBefore).ToList();
        Assert.All(later, r => Assert.Equal($"LOG/CH1/{CurrentMonth}/01.TSV", r.Path));
        Assert.Equal(today.Length, later.Min(r => r.Offset));
        Assert.Equal(grown, await client.GetByteArrayAsync(DataUrl(device.Id, $"LOG/CH1/{CurrentMonth}/01.TSV")));

        var months = await client.GetFromJsonAsync<JsonElement>($"/api/mobile/devices/{device.Id}/logs?ch=1");
        Assert.Equal([oldMonth, CurrentMonth], months.GetProperty("months").EnumerateArray().Select(m => m.GetString()));

        var days = await client.GetFromJsonAsync<JsonElement>($"/api/mobile/devices/{device.Id}/logs?ch=1&month={CurrentMonth}");
        var day = Assert.Single(days.GetProperty("days").EnumerateArray());
        Assert.Equal($"LOG/CH1/{CurrentMonth}/01.TSV", day.GetProperty("path").GetString());
        Assert.Equal(grown.Length, day.GetProperty("size").GetInt64());
        Assert.False(day.GetProperty("complete").GetBoolean());
        Assert.False(days.TryGetProperty("months", out _));
    }

    [Fact]
    public async Task Data_supports_range_head_and_conditional_requests_byte_exact()
    {
        var server = StartServer(new Dictionary<string, string?> { ["Sync:IntervalS"] = "1" });
        var client = server.CreateClient();
        var device = await SeedDeviceAsync();
        var path = $"LOG/CH1/{CurrentMonth}/02.TSV";
        var content = TsvBytes(200);
        await using var link = await FakeDevice.ConnectAsync(server, device.DeviceCode, TestData.DeviceKey,
            setup: d => d.AddFile(1, CurrentMonth, path, content, closed: false));
        await EventuallyAsync(async () => (await FilesAsync(device.Id)) is [{ StoredSize: > 0 } f] && f.StoredSize == content.Length, "the file is copied");

        var full = await client.GetAsync(DataUrl(device.Id, path));
        Assert.Equal(HttpStatusCode.OK, full.StatusCode);
        Assert.Equal("text/tab-separated-values", full.Content.Headers.ContentType!.ToString());
        Assert.Equal("bytes", full.Headers.AcceptRanges.Single());
        Assert.Equal(content, await full.Content.ReadAsByteArrayAsync());
        var etag = full.Headers.ETag!;

        var range = new HttpRequestMessage(HttpMethod.Get, DataUrl(device.Id, path));
        range.Headers.Range = new RangeHeaderValue(100, 199);
        var partial = await client.SendAsync(range);
        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.Equal(content.AsSpan(100, 100).ToArray(), await partial.Content.ReadAsByteArrayAsync());

        var tail = new HttpRequestMessage(HttpMethod.Get, DataUrl(device.Id, path));
        tail.Headers.Range = new RangeHeaderValue(content.Length - 10, null);
        Assert.Equal(content[^10..], await (await client.SendAsync(tail)).Content.ReadAsByteArrayAsync());

        var outside = new HttpRequestMessage(HttpMethod.Get, DataUrl(device.Id, path));
        outside.Headers.Range = new RangeHeaderValue(content.Length, null);
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, (await client.SendAsync(outside)).StatusCode);

        var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, DataUrl(device.Id, path)));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(content.Length, head.Content.Headers.ContentLength);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());

        var conditional = new HttpRequestMessage(HttpMethod.Get, DataUrl(device.Id, path));
        conditional.Headers.IfNoneMatch.Add(etag);
        Assert.Equal(HttpStatusCode.NotModified, (await client.SendAsync(conditional)).StatusCode);

        // Growth changes the ETag.
        var grown = content.Concat(TsvBytes(1, "9")).ToArray();
        link.AddFile(1, CurrentMonth, path, grown, closed: true);
        await EventuallyAsync(async () => (await FilesAsync(device.Id)) is [var f] && f.StoredSize == grown.Length, "the tail is copied");
        var after = await client.GetAsync(DataUrl(device.Id, path));
        Assert.NotEqual(etag, after.Headers.ETag);
        Assert.Equal(grown, await after.Content.ReadAsByteArrayAsync());

        await EventuallyAsync(async () =>
        {
            var days = await client.GetFromJsonAsync<JsonElement>($"/api/mobile/devices/{device.Id}/logs?ch=1&month={CurrentMonth}");
            return days.GetProperty("days")[0].GetProperty("complete").GetBoolean();
        }, "the closed and fully copied day is complete");
    }

    [Fact]
    public async Task A_missing_copy_is_sync_pending_while_online_and_then_served()
    {
        var server = StartServer();
        var client = server.CreateClient();
        var device = await SeedDeviceAsync();
        var path = $"LOG/CH2/{MonthsAgo(1)}/15.TSV";
        var content = TsvBytes(50);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var link = await FakeDevice.ConnectAsync(server, device.DeviceCode, TestData.DeviceKey, setup: d =>
        {
            d.AddFile(2, MonthsAgo(1), path, content, closed: true);
            d.ReadGate = gate;
        });
        await EventuallyAsync(async () => (await FilesAsync(device.Id)).Count == 1, "the file is listed");

        var pending = await client.GetAsync(DataUrl(device.Id, path));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, pending.StatusCode);
        Assert.Equal("sync.pending", await CodeAsync(pending));
        Assert.True(pending.Headers.RetryAfter!.Delta > TimeSpan.Zero);

        gate.SetResult();
        await EventuallyAsync(async () => (await client.GetAsync(DataUrl(device.Id, path))).StatusCode == HttpStatusCode.OK, "the copy is served");
        Assert.Equal(content, await client.GetByteArrayAsync(DataUrl(device.Id, path)));
    }

    [Fact]
    public async Task Copies_are_served_while_the_device_is_offline_and_missing_ones_are_device_offline()
    {
        var server = StartServer();
        var client = server.CreateClient();
        var device = await SeedDeviceAsync(d => d.LinkHello = FakeDevice.Hello("files").ToJsonString());
        var content = TsvBytes(20);
        var month = MonthsAgo(2);

        await server.Services.GetRequiredService<LogFileStorage>().WriteAsync(device.Id, $"LOG/CH1/{month}/01.TSV", 0, content, CancellationToken.None);
        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            db.DeviceLogFiles.AddRange(
                new DeviceLogFile { DeviceId = device.Id, Channel = 1, Month = month, Path = $"LOG/CH1/{month}/01.TSV", DeviceSize = content.Length, StoredSize = content.Length, Closed = true },
                new DeviceLogFile { DeviceId = device.Id, Channel = 1, Month = month, Path = $"LOG/CH1/{month}/02.TSV", DeviceSize = 500, Closed = true });
            await db.SaveChangesAsync();
        }

        Assert.Equal(content, await client.GetByteArrayAsync(DataUrl(device.Id, $"LOG/CH1/{month}/01.TSV")));

        var missing = await client.GetAsync(DataUrl(device.Id, $"LOG/CH1/{month}/02.TSV"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, missing.StatusCode);
        Assert.Equal("device.offline", await CodeAsync(missing));

        var days = await client.GetFromJsonAsync<JsonElement>($"/api/mobile/devices/{device.Id}/logs?ch=1&month={month}");
        Assert.Equal([true, false], days.GetProperty("days").EnumerateArray().Select(d => d.GetProperty("complete").GetBoolean()));
        Assert.Equal([content.Length, 0L], days.GetProperty("days").EnumerateArray().Select(d => d.GetProperty("size").GetInt64()));
    }

    [Fact]
    public async Task Log_listing_validates_its_parameters()
    {
        var server = StartServer();
        var client = server.CreateClient();
        var device = await SeedDeviceAsync(d => d.LinkHello = FakeDevice.Hello("files").ToJsonString());

        foreach (var (query, status, code) in new[]
                 {
                     ("", HttpStatusCode.BadRequest, "logs.invalid_channel"),
                     ("?ch=abc", HttpStatusCode.BadRequest, "logs.invalid_channel"),
                     ("?ch=-1", HttpStatusCode.BadRequest, "logs.invalid_channel"),
                     ("?ch=1&month=2026-13", HttpStatusCode.BadRequest, "logs.invalid_month"),
                     ("?ch=1&month=2020-01", HttpStatusCode.NotFound, "log.not_found"),
                 })
        {
            var response = await client.GetAsync($"/api/mobile/devices/{device.Id}/logs{query}");
            Assert.Equal(status, response.StatusCode);
            Assert.Equal(code, await CodeAsync(response));
        }

        var months = await client.GetFromJsonAsync<JsonElement>($"/api/mobile/devices/{device.Id}/logs?ch=0");
        Assert.Equal(0, months.GetProperty("months").GetArrayLength());
    }

    [Fact]
    public async Task A_shrunk_file_is_discarded_and_read_again()
    {
        var server = StartServer(new Dictionary<string, string?> { ["Sync:IntervalS"] = "1" });
        var client = server.CreateClient();
        var device = await SeedDeviceAsync();
        var path = $"LOG/CH1/{CurrentMonth}/03.TSV";
        var original = TsvBytes(100);
        await using var link = await FakeDevice.ConnectAsync(server, device.DeviceCode, TestData.DeviceKey,
            setup: d => d.AddFile(1, CurrentMonth, path, original, closed: false));
        await EventuallyAsync(async () => (await FilesAsync(device.Id)) is [var f] && f.StoredSize == original.Length, "the file is copied");

        var replaced = TsvBytes(10, "7");
        link.AddFile(1, CurrentMonth, path, replaced, closed: false);

        await EventuallyAsync(async () => (await FilesAsync(device.Id)).Single() is { Generation: 1 } f && f.StoredSize == replaced.Length,
            "the file is read again");
        Assert.Equal(replaced, await client.GetByteArrayAsync(DataUrl(device.Id, path)));
    }

    [Fact]
    public async Task A_file_gone_from_the_device_keeps_its_copy_but_is_hidden()
    {
        var server = StartServer(new Dictionary<string, string?> { ["Sync:IntervalS"] = "1" });
        var client = server.CreateClient();
        var device = await SeedDeviceAsync();
        var keep = $"LOG/CH1/{CurrentMonth}/01.TSV";
        var gone = $"LOG/CH1/{CurrentMonth}/02.TSV";
        await using var link = await FakeDevice.ConnectAsync(server, device.DeviceCode, TestData.DeviceKey, setup: d =>
        {
            d.AddFile(1, CurrentMonth, keep, TsvBytes(5), closed: true);
            d.AddFile(1, CurrentMonth, gone, TsvBytes(5), closed: true);
        });
        await EventuallyAsync(async () => (await FilesAsync(device.Id)) is { Count: 2 } files && files.All(f => f.StoredSize > 0), "both files are copied");

        link.Files.TryRemove(gone, out _);

        await EventuallyAsync(async () => (await FilesAsync(device.Id)).Single(f => f.Path == gone).Deleted, "the file is marked deleted");
        var days = await client.GetFromJsonAsync<JsonElement>($"/api/mobile/devices/{device.Id}/logs?ch=1&month={CurrentMonth}");
        Assert.Equal([keep], days.GetProperty("days").EnumerateArray().Select(d => d.GetProperty("path").GetString()));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(DataUrl(device.Id, gone))).StatusCode);

        var row = (await FilesAsync(device.Id)).Single(f => f.Path == gone);
        Assert.True(row.StoredSize > 0);
    }

    [Theory]
    [InlineData("../../appsettings.json")]
    [InlineData("..%2F..%2Fappsettings.json")]
    [InlineData("C:\\Windows\\win.ini")]
    public async Task Paths_outside_the_known_files_are_404_without_disk_access(string path)
    {
        var server = StartServer();
        var client = server.CreateClient();
        var device = await SeedDeviceAsync(d => d.LinkHello = FakeDevice.Hello("files").ToJsonString());

        var response = await client.GetAsync($"/api/mobile/devices/{device.Id}/data/{Uri.EscapeDataString(path)}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("log.not_found", await CodeAsync(response));
    }

    [Fact]
    public async Task Unknown_ops_and_fields_are_ignored()
    {
        var server = StartServer();
        var device = await SeedDeviceAsync();
        await using var link = await FakeDevice.ConnectAsync(server, device.DeviceCode, TestData.DeviceKey);

        await link.SendAsync(new JsonObject { ["op"] = "something.new", ["x"] = 1 });
        await link.SendAsync(new JsonObject { ["op"] = "file.data", ["reqId"] = 999_999 });

        await using var viewer = await LiveViewer.ConnectAsync(server, device.Id);
        await viewer.ExpectStatusAsync(LiveHub.Connecting);
        Assert.Null(link.CloseStatus);
    }
}
