using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;

namespace DeviceTelemetry.IntegrationTests.DeviceLink;

/// <summary>A mobile client on <c>/api/mobile/devices/{id}/live</c>.</summary>
public sealed class LiveViewer : IAsyncDisposable
{
    private readonly WebSocket _socket;

    private LiveViewer(WebSocket socket) => _socket = socket;

    public WebSocketCloseStatus? CloseStatus => _socket.CloseStatus;

    public static async Task<LiveViewer> ConnectAsync(TestServer server, int deviceId) =>
        new(await server.CreateWebSocketClient().ConnectAsync(
            new Uri(server.BaseAddress, $"api/mobile/devices/{deviceId}/live"), CancellationToken.None));

    /// <summary>The next frame, or <c>null</c> when the server closed the stream.</summary>
    public async Task<JsonElement?> NextAsync(TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        var buffer = new byte[16 * 1024];
        var count = 0;
        WebSocketReceiveResult result;
        do
        {
            result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer, count, buffer.Length - count), cts.Token);
            count += result.Count;
        }
        while (!result.EndOfMessage);

        if (result.MessageType == WebSocketMessageType.Close)
            return null;

        using var document = JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, count));
        return document.RootElement.Clone();
    }

    /// <summary>Skips frames until a status frame with <paramref name="state"/>.</summary>
    public async Task ExpectStatusAsync(string state)
    {
        while (true)
        {
            var frame = await NextAsync() ?? throw new InvalidOperationException($"Stream closed while waiting for status '{state}'.");
            if (frame.GetProperty("type").GetString() == "status" && frame.GetProperty("state").GetString() == state)
                return;
        }
    }

    /// <summary>Skips frames until a values frame.</summary>
    public async Task<JsonElement> ExpectValuesAsync()
    {
        while (true)
        {
            var frame = await NextAsync() ?? throw new InvalidOperationException("Stream closed while waiting for values.");
            if (frame.GetProperty("type").GetString() == "values")
                return frame;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException)
        {
        }

        _socket.Dispose();
    }
}
