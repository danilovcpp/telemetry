using System.Net.WebSockets;
using System.Text.Json;
using DeviceTelemetry.Application.Mobile;
using DeviceTelemetry.Infrastructure.DeviceLink;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DeviceTelemetry.UnitTests.DeviceLink;

public sealed class LiveHubTests
{
    private static (LiveHub Hub, DeviceLinkSession Session) Connected(params string[] caps)
    {
        var hub = new LiveHub(Options.Create(new LiveOptions()), TimeProvider.System, NullLogger<LiveHub>.Instance);
        var hello = JsonSerializer.Serialize(new { op = "hello", link = 1, schema = 1, caps });
        using var document = JsonDocument.Parse(hello);
        var session = new DeviceLinkSession(7, new SinkWebSocket(), CancellationToken.None) { Hello = LinkHello.Parse(hello, document.RootElement) };
        hub.OnLinkUp(session);
        return (hub, session);
    }

    private static LiveSubscription Viewer(LiveHub hub, string address = "10.0.0.1")
    {
        var viewer = hub.TrySubscribe(7, address, out _)!;
        hub.Activate(viewer);
        return viewer;
    }

    private static JsonElement Values(int t)
    {
        using var document = JsonDocument.Parse($$"""{"op":"values","t":{{t}},"atUtc":"2026-10-23T10:00:00Z","values":[{"id":"ch1","t":{{t}},"value":1}]}""");
        return document.RootElement.Clone();
    }

    [Fact]
    public void A_slow_viewer_keeps_at_most_two_frames_and_loses_values_before_statuses()
    {
        var (hub, session) = Connected("live");
        using var viewer = Viewer(hub);

        for (var t = 1; t <= 1000; t++)
            hub.OnValues(session, Values(t));

        // connecting and streaming were queued first; values pushed out older values, then the oldest status.
        Assert.Equal(2, viewer.Count);
        Assert.True(viewer.TryRead(out var first));
        Assert.True(viewer.TryRead(out var second));
        Assert.Contains("\"state\":\"streaming\"", first);
        Assert.Contains("\"t\":1000", second);
        Assert.False(viewer.TryRead(out _));
    }

    [Fact]
    public async Task The_stream_ends_after_the_queued_frames_when_the_device_refuses()
    {
        var (hub, session) = Connected("live");
        using var viewer = Viewer(hub);

        hub.OnError(session, "not_supported", null);

        var frames = new List<string>();
        await foreach (var frame in viewer.ReadAllAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token))
            frames.Add(frame);
        Assert.Equal(2, frames.Count);
        Assert.Contains("\"state\":\"connecting\"", frames[0]);
        Assert.Contains("\"state\":\"not_supported\"", frames[1]);
    }

    [Fact]
    public void A_reserved_viewer_gets_nothing_until_it_is_activated()
    {
        var (hub, session) = Connected("live");
        using var viewer = hub.TrySubscribe(7, "10.0.0.1", out _)!;
        hub.OnValues(session, Values(1));
        Assert.Equal(0, viewer.Count);

        hub.Activate(viewer);

        Assert.True(viewer.TryRead(out var status));
        Assert.Contains("\"state\":\"connecting\"", status);
    }

    [Fact]
    public void Viewer_limits_apply_per_address_and_per_device()
    {
        var hub = new LiveHub(
            Options.Create(new LiveOptions { MaxViewersPerIp = 2, MaxViewersPerDevice = 3 }), TimeProvider.System, NullLogger<LiveHub>.Instance);

        using var a = hub.TrySubscribe(1, "a", out _);
        using var b = hub.TrySubscribe(1, "a", out _);
        Assert.Null(hub.TrySubscribe(1, "a", out var refusal));
        Assert.Equal(LiveSubscribeRefusal.TooManyForAddress, refusal);

        using var c = hub.TrySubscribe(1, "b", out _);
        Assert.Null(hub.TrySubscribe(1, "c", out refusal));
        Assert.Equal(LiveSubscribeRefusal.TooManyForDevice, refusal);

        a!.Dispose();
        using var d = hub.TrySubscribe(1, "a", out refusal);
        Assert.NotNull(d);
    }

    [Fact]
    public void Values_of_a_replaced_connection_are_dropped()
    {
        var (hub, old) = Connected("live");
        using var viewer = Viewer(hub, "x");
        viewer.TryRead(out _);

        using var document = JsonDocument.Parse("""{"op":"hello","link":1,"caps":["live"]}""");
        var replacement = new DeviceLinkSession(7, new SinkWebSocket(), CancellationToken.None)
        {
            Hello = LinkHello.Parse(document.RootElement.GetRawText(), document.RootElement),
        };
        hub.OnLinkUp(replacement);
        hub.OnLinkDown(old);
        while (viewer.TryRead(out _))
        {
        }

        hub.OnValues(old, Values(1));

        Assert.Equal(0, viewer.Count);
    }

    /// <summary>Accepts and discards everything that is sent.</summary>
    private sealed class SinkWebSocket : WebSocket
    {
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Dispose() { }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
            Task.Delay(Timeout.Infinite, cancellationToken).ContinueWith(_ => new WebSocketReceiveResult(0, WebSocketMessageType.Close, true), TaskScheduler.Default);

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}

public sealed class LogPathsTests
{
    [Theory]
    [InlineData("LOG/CH1/2026-10/23.TSV")]
    [InlineData("/LOG/SUM/2026-10/01.TSV")]
    [InlineData("data_1-a.tsv")]
    public void Device_paths_are_accepted(string path) => Assert.True(LogPaths.IsValid(path));

    [Theory]
    [InlineData("")]
    [InlineData("../etc/passwd")]
    [InlineData("LOG/../../x")]
    [InlineData("LOG/./x")]
    [InlineData("LOG//x")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("C:/x")]
    [InlineData("LOG/x%2F..")]
    [InlineData("LOG/a b")]
    [InlineData("LOG/x/")]
    public void Anything_else_is_rejected(string path) => Assert.False(LogPaths.IsValid(path));

    [Fact]
    public void Overlong_paths_are_rejected() => Assert.False(LogPaths.IsValid(new string('a', 257)));
}

public sealed class LogFileStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "devicetelemetry-unit", Guid.NewGuid().ToString("N"));
    private readonly LogFileStorage _storage;

    public LogFileStorageTests() => _storage = new LogFileStorage(Options.Create(new StorageOptions { DataRoot = _root }));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Appends_at_the_offset_and_serves_only_the_copied_length()
    {
        await _storage.WriteAsync(3, "LOG/a.tsv", 0, "hello "u8.ToArray(), CancellationToken.None);
        await _storage.WriteAsync(3, "LOG/a.tsv", 6, "world"u8.ToArray(), CancellationToken.None);

        Assert.Equal("hello world"u8.ToArray(), await File.ReadAllBytesAsync(Path.Combine(_root, "3", "LOG", "a.tsv")));

        await using var stream = _storage.OpenRead(3, "LOG/a.tsv", 5)!;
        Assert.Equal(5, stream.Length);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        Assert.Equal("hello"u8.ToArray(), copy.ToArray());
    }

    [Fact]
    public async Task Bytes_after_the_offset_are_cut_off_before_appending()
    {
        await _storage.WriteAsync(3, "a.tsv", 0, "abcdef"u8.ToArray(), CancellationToken.None);
        await _storage.WriteAsync(3, "a.tsv", 3, "X"u8.ToArray(), CancellationToken.None);

        Assert.Equal("abcX"u8.ToArray(), await File.ReadAllBytesAsync(Path.Combine(_root, "3", "a.tsv")));
    }

    [Fact]
    public async Task A_gap_is_refused()
    {
        await _storage.WriteAsync(3, "a.tsv", 0, "ab"u8.ToArray(), CancellationToken.None);

        await Assert.ThrowsAsync<InvalidDataException>(() => _storage.WriteAsync(3, "a.tsv", 5, "x"u8.ToArray(), CancellationToken.None));
    }

    [Theory]
    [InlineData("../4/a.tsv")]
    [InlineData("../../outside.tsv")]
    public async Task Paths_never_leave_the_device_directory(string path)
    {
        Assert.Null(_storage.OpenRead(3, path, 1));
        await Assert.ThrowsAsync<ArgumentException>(() => _storage.WriteAsync(3, path, 0, "x"u8.ToArray(), CancellationToken.None));
        Assert.False(Directory.Exists(Path.Combine(_root, "4")));
    }

    [Fact]
    public void Missing_copy_is_null_and_an_empty_one_is_empty()
    {
        Assert.Null(_storage.OpenRead(3, "none.tsv", 10));
        Assert.Equal(0, _storage.OpenRead(3, "none.tsv", 0)!.Length);
    }
}

public sealed class MobileFeaturesTests
{
    private const string Hello = """{"op":"hello","link":1,"caps":["live","files"]}""";

    private static MobileFeatureOptions On => new() { DeviceLinkEnabled = true, LiveEnabled = true, HistoryEnabled = true };

    [Fact]
    public void Capabilities_of_the_hello_become_features() =>
        Assert.Equal(["live", "history"], MobileFeatures.For(On, Hello, hasCopies: false));

    [Fact]
    public void No_hello_means_no_features() => Assert.Empty(MobileFeatures.For(On, null, hasCopies: false));

    [Fact]
    public void Copies_keep_history_even_when_the_link_is_off() =>
        Assert.Equal(["history"], MobileFeatures.For(new MobileFeatureOptions { HistoryEnabled = true }, null, hasCopies: true));

    [Fact]
    public void Switched_off_features_are_never_listed() =>
        Assert.Empty(MobileFeatures.For(new MobileFeatureOptions { DeviceLinkEnabled = true }, Hello, hasCopies: true));

    [Fact]
    public void Live_needs_the_link() =>
        Assert.Empty(MobileFeatures.For(new MobileFeatureOptions { LiveEnabled = true }, Hello, hasCopies: false));
}

public sealed class LinkHelloTests
{
    [Fact]
    public void Channel_numbers_come_from_numeric_or_named_ids_and_unknown_fields_are_kept()
    {
        const string raw = """
            {"op":"hello","link":1,"schema":"s2","caps":["live",5],"future":true,
             "channels":[{"id":"ch2"},{"id":1},{"id":"x"},{"id":0},{"name":"no id"}]}
            """;
        using var document = JsonDocument.Parse(raw);

        var hello = LinkHello.Parse(raw, document.RootElement);

        Assert.Equal(1, hello.Link);
        Assert.Equal("s2", hello.Schema!.Value.GetString());
        Assert.Equal(["live"], hello.Caps);
        Assert.Equal([1, 2], hello.Channels);
        Assert.Contains("\"future\":true", hello.Raw);
    }

    [Fact]
    public void Missing_link_is_version_0()
    {
        using var document = JsonDocument.Parse("""{"op":"hello"}""");
        Assert.Equal(0, LinkHello.Parse("{}", document.RootElement).Link);
    }
}
