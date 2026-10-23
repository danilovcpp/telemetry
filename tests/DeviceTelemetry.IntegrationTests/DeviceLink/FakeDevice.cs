using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.TestHost;

namespace DeviceTelemetry.IntegrationTests.DeviceLink;

/// <summary>
/// A device on the other end of Device Link, behaving like the SG Manager firmware: answers <c>logs.*</c> and
/// <c>file.read</c> from <see cref="Files"/> with at most 4 KB per answer and extra fields (<c>unknown</c>,
/// <c>complete</c>), counts <c>live.start</c>/<c>live.stop</c> and sends <c>values</c> when told to.
/// </summary>
public sealed class FakeDevice : IAsyncDisposable
{
    public const int MaxChunk = 4096;

    private readonly WebSocket _socket;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _welcome = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _loop = Task.CompletedTask;

    private FakeDevice(WebSocket socket) => _socket = socket;

    /// <summary>Device files by path.</summary>
    public ConcurrentDictionary<string, DeviceFile> Files { get; } = new();

    /// <summary>Every <c>file.read</c> as (path, offset).</summary>
    public ConcurrentQueue<(string Path, long Offset)> Reads { get; } = new();

    /// <summary>When set, <c>file.read</c> answers wait for it.</summary>
    public TaskCompletionSource? ReadGate { get; set; }

    /// <summary>Error code to answer <c>live.start</c> with (e.g. <c>not_supported</c>).</summary>
    public string? LiveStartError { get; set; }

    public int LiveStarts => _liveStarts;
    public int LiveStops => _liveStops;
    private int _liveStarts;
    private int _liveStops;

    public WebSocketCloseStatus? CloseStatus => _socket.CloseStatus;
    public Task Finished => _loop;

    public static JsonObject Hello(params string[] caps) => new()
    {
        ["op"] = "hello",
        ["link"] = 1,
        ["schema"] = 3,
        ["fw"] = "1.0.0-test",
        ["bootId"] = "b-1",
        ["chipUid"] = "0011223344",
        ["clockValid"] = true,
        ["utcOffsetMin"] = 120,
        ["tzName"] = "Europe/Berlin",
        ["caps"] = new JsonArray(caps.Select(c => (JsonNode)c).ToArray()),
        ["channels"] = new JsonArray(
            new JsonObject { ["id"] = "ch1", ["name"] = "Temperature", ["unit"] = "°C", ["type"] = "analog" },
            new JsonObject { ["id"] = "ch2", ["name"] = "Pressure", ["unit"] = "bar", ["type"] = "analog" }),
        ["unknown"] = "ignored by the server",
    };

    public static async Task<WebSocket> OpenAsync(TestServer server, string deviceCode, string deviceKey)
    {
        var client = server.CreateWebSocketClient();
        client.ConfigureRequest = request =>
        {
            request.Headers["X-Device-Id"] = deviceCode;
            request.Headers["X-Device-Key"] = deviceKey;
        };
        return await client.ConnectAsync(new Uri(server.BaseAddress, "api/device/v1/link"), CancellationToken.None);
    }

    /// <summary>Connects, sends <paramref name="hello"/> and waits for <c>welcome</c>.</summary>
    public static async Task<FakeDevice> ConnectAsync(TestServer server, string deviceCode, string deviceKey, JsonObject? hello = null,
        Action<FakeDevice>? setup = null)
    {
        var device = new FakeDevice(await OpenAsync(server, deviceCode, deviceKey));
        setup?.Invoke(device);
        device._loop = Task.Run(device.RunAsync);
        await device.SendAsync(hello ?? Hello("live", "files"));
        await device._welcome.Task.WaitAsync(TimeSpan.FromSeconds(10));
        return device;
    }

    public void AddFile(int channel, string month, string path, byte[] content, bool closed) =>
        Files[path] = new DeviceFile(channel, month, content, closed);

    public Task SendValuesAsync(int t) => SendAsync(new JsonObject
    {
        ["op"] = "values",
        ["t"] = t,
        ["atUtc"] = "2026-10-23T10:00:00Z",
        ["values"] = new JsonArray(
            new JsonObject { ["id"] = "ch1", ["t"] = t, ["value"] = 21.5 + t },
            new JsonObject { ["id"] = "ch2", ["t"] = t, ["value"] = 1.25 }),
    });

    public async Task SendAsync(JsonObject message)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        await _sendLock.WaitAsync();
        try
        {
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Drops the connection without a close handshake (power loss).</summary>
    public async Task DropAsync()
    {
        _socket.Abort();
        await _stop.CancelAsync();
        await _loop.WaitAsync(TimeSpan.FromSeconds(10)).ContinueWith(_ => { });
    }

    private async Task RunAsync()
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var count = 0;
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer, count, buffer.Length - count), _stop.Token);
                    count += result.Count;
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close)
                    return;

                var message = JsonNode.Parse(buffer.AsSpan(0, count))!.AsObject();
                _ = Task.Run(() => HandleAsync(message));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException)
        {
        }
    }

    private async Task HandleAsync(JsonObject message)
    {
        var reqId = message["reqId"]?.DeepClone();
        switch ((string?)message["op"])
        {
            case "welcome":
                _welcome.TrySetResult();
                break;

            case "live.start":
                Interlocked.Increment(ref _liveStarts);
                if (LiveStartError is { } error)
                    await SendAsync(new JsonObject { ["op"] = "error", ["for"] = "live.start", ["code"] = error });
                break;

            case "live.stop":
                Interlocked.Increment(ref _liveStops);
                break;

            case "logs.months":
            {
                var ch = (int)message["ch"]!;
                var months = Files.Values.Where(f => f.Channel == ch).Select(f => f.Month).Distinct().Order();
                await SendAsync(new JsonObject
                {
                    ["op"] = "logs.months", ["reqId"] = reqId, ["months"] = new JsonArray(months.Select(m => (JsonNode)m).ToArray()),
                });
                break;
            }

            case "logs.days":
            {
                var ch = (int)message["ch"]!;
                var month = (string)message["month"]!;
                var days = Files.Where(f => f.Value.Channel == ch && f.Value.Month == month).OrderBy(f => f.Key)
                    .Select(f => (JsonNode)new JsonObject
                    {
                        ["path"] = f.Key, ["size"] = f.Value.Content.Length, ["closed"] = f.Value.Closed, ["complete"] = f.Value.Closed,
                    });
                await SendAsync(new JsonObject { ["op"] = "logs.days", ["reqId"] = reqId, ["days"] = new JsonArray(days.ToArray()) });
                break;
            }

            case "file.read":
            {
                if (ReadGate is { } gate)
                    await gate.Task;

                var path = (string)message["path"]!;
                var offset = (long)message["offset"]!;
                var length = (int)message["length"]!;
                Reads.Enqueue((path, offset));

                if (!Files.TryGetValue(path, out var file))
                {
                    await SendAsync(new JsonObject { ["op"] = "error", ["for"] = "file.read", ["reqId"] = reqId, ["code"] = "not_found" });
                    break;
                }

                var take = (int)Math.Max(0, Math.Min(Math.Min(length, MaxChunk), file.Content.Length - offset));
                var data = offset <= file.Content.Length ? file.Content.AsSpan((int)offset, take).ToArray() : [];
                await SendAsync(new JsonObject
                {
                    ["op"] = "file.data",
                    ["reqId"] = reqId,
                    ["offset"] = offset,
                    ["size"] = file.Content.Length,
                    ["data"] = Convert.ToBase64String(data),
                    ["eof"] = offset + take >= file.Content.Length,
                    ["unknown"] = 1,
                });
                break;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException)
        {
        }

        await _stop.CancelAsync();
        await _loop.WaitAsync(TimeSpan.FromSeconds(10)).ContinueWith(_ => { });
        _socket.Dispose();
        _stop.Dispose();
        _sendLock.Dispose();
    }
}

public sealed record DeviceFile(int Channel, string Month, byte[] Content, bool Closed);
