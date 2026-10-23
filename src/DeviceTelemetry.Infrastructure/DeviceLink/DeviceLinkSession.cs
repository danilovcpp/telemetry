using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace DeviceTelemetry.Infrastructure.DeviceLink;

/// <summary>Close codes and limits of Device Link v1.</summary>
public static class DeviceLinkProtocol
{
    public const int Version = 1;

    /// <summary>Text frames (JSON) in both directions are at most this long.</summary>
    public const int MaxFrameBytes = 16 * 1024;

    /// <summary>The device sent a <c>hello</c> with a <c>link</c> version the server does not speak.</summary>
    public const WebSocketCloseStatus IncompatibleLink = (WebSocketCloseStatus)4400;

    /// <summary>A newer connection of the same device replaced this one.</summary>
    public const WebSocketCloseStatus Replaced = (WebSocketCloseStatus)4409;

    public const string DeviceIdHeader = "X-Device-Id";
}

/// <summary>The device's last <c>hello</c>: what it can do and which channels it has.</summary>
public sealed partial record LinkHello(string Raw, int Link, JsonElement? Schema, IReadOnlySet<string> Caps, IReadOnlyList<int> Channels)
{
    public bool Supports(string capability) => Caps.Contains(capability);

    [GeneratedRegex(@"(\d+)$")]
    private static partial Regex TrailingNumber();

    /// <summary>Parses a <c>hello</c> frame; unknown fields are ignored and kept in <see cref="Raw"/>.</summary>
    public static LinkHello Parse(string raw, JsonElement root)
    {
        var link = root.TryGetProperty("link", out var l) && l.ValueKind == JsonValueKind.Number && l.TryGetInt32(out var v) ? v : 0;
        JsonElement? schema = root.TryGetProperty("schema", out var s) ? s.Clone() : null;

        var caps = new HashSet<string>(StringComparer.Ordinal);
        if (root.TryGetProperty("caps", out var c) && c.ValueKind == JsonValueKind.Array)
        {
            foreach (var cap in c.EnumerateArray())
            {
                if (cap.ValueKind == JsonValueKind.String)
                    caps.Add(cap.GetString()!);
            }
        }

        // Channel ids are numbers or strings ending in the number ("ch2"); the file requests use that number.
        var channels = new SortedSet<int>();
        if (root.TryGetProperty("channels", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var channel in list.EnumerateArray())
            {
                if (channel.ValueKind != JsonValueKind.Object || !channel.TryGetProperty("id", out var id))
                    continue;

                if (id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out var n) && n > 0)
                    channels.Add(n);
                else if (id.ValueKind == JsonValueKind.String
                         && TrailingNumber().Match(id.GetString()!) is { Success: true } m
                         && int.TryParse(m.Value, out n) && n is > 0 and < 1000)
                    channels.Add(n);
            }
        }

        return new LinkHello(raw, link, schema, caps, channels.ToList());
    }
}

/// <summary>An error frame from the device answering a request: <c>{"op":"error","code":…,"retryAfterS":…}</c>.</summary>
public sealed class DeviceLinkErrorException(string code, int? retryAfterS)
    : Exception($"The device answered with error '{code}'.")
{
    public string Code { get; } = code;
    public int? RetryAfterS { get; } = retryAfterS;
}

/// <summary>
/// One Device Link connection. Sends are serialized (one frame at a time); requests carry a numeric <c>reqId</c> and are
/// matched with the answer that echoes it, whatever its <c>op</c>.
/// </summary>
public sealed class DeviceLinkSession : IDisposable
{
    private readonly WebSocket _socket;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _closed;
    private readonly Channel<string> _onDemand = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private long _nextRequestId;

    public DeviceLinkSession(int deviceId, WebSocket socket, CancellationToken connectionAborted)
    {
        DeviceId = deviceId;
        _socket = socket;
        _closed = CancellationTokenSource.CreateLinkedTokenSource(connectionAborted);
    }

    public int DeviceId { get; }

    public Guid ConnectionId { get; } = Guid.CreateVersion7();

    /// <summary>The last <c>hello</c> on this connection; set before the session is registered.</summary>
    public LinkHello Hello { get; set; } = null!;

    /// <summary>Cancelled when the connection ends or is replaced.</summary>
    public CancellationToken Closed => _closed.Token;

    /// <summary>Files a viewer is waiting for, read before anything else.</summary>
    public ChannelReader<string> OnDemandFiles => _onDemand.Reader;

    public void RequestFileSync(string path) => _onDemand.Writer.TryWrite(path);

    public async Task SendAsync(JsonObject message, CancellationToken ct = default)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        if (bytes.Length > DeviceLinkProtocol.MaxFrameBytes)
            throw new InvalidOperationException("Device Link frame exceeds the frame size limit.");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, Closed);
        await _sendLock.WaitAsync(linked.Token);
        try
        {
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, linked.Token);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Sends a request and waits for the frame with the same <c>reqId</c>.</summary>
    /// <exception cref="DeviceLinkErrorException">The device answered with an <c>error</c> frame.</exception>
    /// <exception cref="TimeoutException">No answer within <paramref name="timeout"/>.</exception>
    public async Task<JsonElement> RequestAsync(JsonObject request, TimeSpan timeout, CancellationToken ct)
    {
        var requestId = Interlocked.Increment(ref _nextRequestId);
        var answer = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = answer;

        try
        {
            request["reqId"] = requestId;
            await SendAsync(request, ct);

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, Closed);
            var response = await answer.Task.WaitAsync(timeout, linked.Token);

            if (response.TryGetProperty("op", out var op) && op.ValueEquals("error"))
            {
                var code = response.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : "unknown";
                int? retryAfter = response.TryGetProperty("retryAfterS", out var r) && r.TryGetInt32(out var seconds) ? seconds : null;
                throw new DeviceLinkErrorException(code, retryAfter);
            }

            return response;
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
        }
    }

    /// <summary>Completes the request a frame answers. Returns <c>false</c> when the frame answers no open request.</summary>
    public bool TryCompleteRequest(JsonElement frame)
    {
        if (!frame.TryGetProperty("reqId", out var id))
            return false;

        long requestId;
        if (id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var n))
            requestId = n;
        else if (id.ValueKind == JsonValueKind.String && long.TryParse(id.GetString(), out n))
            requestId = n;
        else
            return false;

        return _pending.TryRemove(requestId, out var answer) && answer.TrySetResult(frame.Clone());
    }

    /// <summary>Sends a close frame and ends the session; the receive loop stops.</summary>
    public async Task CloseAsync(WebSocketCloseStatus status, string reason)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _sendLock.WaitAsync(timeout.Token);
            try
            {
                if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await _socket.CloseOutputAsync(status, reason, timeout.Token);
            }
            finally
            {
                _sendLock.Release();
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // The connection is going away anyway.
        }
        finally
        {
            End();
        }
    }

    /// <summary>Marks the session as ended: pending requests and the sync of this connection stop.</summary>
    public void End()
    {
        try
        {
            _closed.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        End();
        _onDemand.Writer.TryComplete();
        _closed.Dispose();
        _sendLock.Dispose();
    }
}
