using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeviceTelemetry.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DeviceTelemetry.Infrastructure.DeviceLink;

/// <summary>
/// Runs one authenticated Device Link connection: waits for <c>hello</c> (closes with 4400 for another <c>link</c>
/// version), stores it, answers <c>welcome</c>, registers the connection (an older one of the device is closed with 4409),
/// then dispatches frames until the connection ends: answers to requests by <c>reqId</c>, <c>values</c> and live errors to
/// <see cref="LiveHub"/>, repeated <c>hello</c>s. Unknown ops are ignored. Frame contents are never logged.
/// </summary>
public sealed class DeviceLinkHandler(
    DeviceLinkRegistry registry,
    LiveHub liveHub,
    LogSynchronizer synchronizer,
    IServiceScopeFactory scopeFactory,
    IOptions<DeviceLinkOptions> options,
    TimeProvider timeProvider,
    ILogger<DeviceLinkHandler> logger)
{
    public async Task RunAsync(WebSocket socket, int deviceId, CancellationToken connectionAborted)
    {
        using var session = new DeviceLinkSession(deviceId, socket, connectionAborted);
        Task? sync = null;
        var registered = false;

        try
        {
            var buffer = new byte[DeviceLinkProtocol.MaxFrameBytes];

            using (var helloTimeout = CancellationTokenSource.CreateLinkedTokenSource(session.Closed))
            {
                helloTimeout.CancelAfter(options.Value.HelloTimeout);

                while (session.Hello is null)
                {
                    var frame = await ReceiveAsync(socket, buffer, helloTimeout.Token);
                    if (frame is null)
                        return;

                    using (frame.Value.Document)
                    {
                        if (frame.Value.Op != "hello")
                        {
                            logger.LogDebug("Device {DeviceId}: '{Op}' before hello ignored", deviceId, Truncate(frame.Value.Op));
                            continue;
                        }

                        if (!await AcceptHelloAsync(session, frame.Value, connectionAborted))
                            return;
                    }
                }
            }

            await registry.RegisterAsync(session);
            registered = true;
            logger.LogInformation(
                "Device {DeviceId} connected over Device Link (connection {ConnectionId}, caps {Caps})",
                deviceId, session.ConnectionId, string.Join(",", session.Hello.Caps));

            liveHub.OnLinkUp(session);
            sync = Task.Run(() => synchronizer.RunAsync(session, session.Closed), CancellationToken.None);

            while (true)
            {
                var frame = await ReceiveAsync(socket, buffer, session.Closed);
                if (frame is null)
                    return;

                await DispatchAsync(session, frame.Value, connectionAborted);
            }
        }
        catch (OperationCanceledException)
        {
            // Closed by the server (replaced, shut down) or no hello in time.
            if (session.Hello is null && !connectionAborted.IsCancellationRequested)
                logger.LogWarning("Device {DeviceId}: no hello within {Timeout}; connection dropped", deviceId, options.Value.HelloTimeout);
        }
        catch (WebSocketException ex)
        {
            logger.LogInformation("Device {DeviceId}: device link connection lost ({Error})", deviceId, ex.WebSocketErrorCode);
        }
        finally
        {
            session.End();
            if (registered)
            {
                registry.Remove(session);
                liveHub.OnLinkDown(session);
                logger.LogInformation("Device {DeviceId} disconnected (connection {ConnectionId})", deviceId, session.ConnectionId);
            }

            if (sync is not null)
                await sync;
        }
    }

    private async Task DispatchAsync(DeviceLinkSession session, Frame frame, CancellationToken ct)
    {
        var root = frame.Document.RootElement;
        using (frame.Document)
        {
            if (session.TryCompleteRequest(root))
                return;

            switch (frame.Op)
            {
                case "values":
                    liveHub.OnValues(session, root);
                    break;

                case "error" when root.TryGetProperty("for", out var f) && f.ValueKind == JsonValueKind.String
                                  && f.GetString()!.StartsWith("live.", StringComparison.Ordinal):
                    var code = root.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : "unknown";
                    int? retryAfter = root.TryGetProperty("retryAfterS", out var r) && r.TryGetInt32(out var seconds) ? seconds : null;
                    liveHub.OnError(session, code, retryAfter);
                    break;

                case "hello":
                    if (await AcceptHelloAsync(session, frame, ct))
                        liveHub.OnHelloChanged(session);
                    break;

                default:
                    logger.LogInformation("Device {DeviceId}: unknown or unexpected op '{Op}' ignored", session.DeviceId, Truncate(frame.Op));
                    break;
            }
        }
    }

    /// <summary>Validates and stores a <c>hello</c>, answers <c>welcome</c>. Returns <c>false</c> when the link was closed.</summary>
    private async Task<bool> AcceptHelloAsync(DeviceLinkSession session, Frame frame, CancellationToken ct)
    {
        var hello = LinkHello.Parse(frame.Raw, frame.Document.RootElement);
        if (hello.Link != DeviceLinkProtocol.Version)
        {
            logger.LogWarning("Device {DeviceId}: unsupported link version {Link}; connection closed", session.DeviceId, hello.Link);
            await session.CloseAsync(DeviceLinkProtocol.IncompatibleLink, "link version not supported");
            return false;
        }

        session.Hello = hello;

        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.Devices
                .Where(d => d.Id == session.DeviceId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.LinkHello, hello.Raw)
                    .SetProperty(d => d.LinkHelloAtUtc, now), ct);
        }

        await session.SendAsync(new JsonObject { ["op"] = "welcome", ["link"] = DeviceLinkProtocol.Version }, ct);
        return true;
    }

    private readonly record struct Frame(string Op, string Raw, JsonDocument Document);

    /// <summary>Next JSON text frame; <c>null</c> when the connection is closing. Oversized frames close it (1009).</summary>
    private async Task<Frame?> ReceiveAsync(WebSocket socket, byte[] buffer, CancellationToken ct)
    {
        while (true)
        {
            var count = 0;
            ValueWebSocketReceiveResult result;
            do
            {
                if (count == buffer.Length)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.MessageTooBig, "frame too large", CancellationToken.None);
                    return null;
                }

                result = await socket.ReceiveAsync(buffer.AsMemory(count), ct);
                count += result.Count;
            }
            while (!result.EndOfMessage && result.MessageType != WebSocketMessageType.Close);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                if (socket.State == WebSocketState.CloseReceived)
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                return null;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                logger.LogDebug("Binary frame ignored");
                continue;
            }

            string raw;
            JsonDocument document;
            try
            {
                raw = Encoding.UTF8.GetString(buffer, 0, count);
                document = JsonDocument.Parse(buffer.AsMemory(0, count));
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException)
            {
                logger.LogInformation("Frame that is not valid JSON ignored");
                continue;
            }

            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("op", out var op) || op.ValueKind != JsonValueKind.String)
            {
                document.Dispose();
                logger.LogInformation("Frame without an op ignored");
                continue;
            }

            return new Frame(op.GetString()!, raw, document);
        }
    }

    private static string Truncate(string value) => value.Length <= 32 ? value : value[..32];
}
