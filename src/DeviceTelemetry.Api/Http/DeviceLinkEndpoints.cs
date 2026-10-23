using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using DeviceTelemetry.Api.Controllers;
using DeviceTelemetry.Application.Common;
using DeviceTelemetry.Application.Devices.Queries.AuthenticateDevice;
using DeviceTelemetry.Application.Mobile;
using DeviceTelemetry.Application.Mobile.Queries.GetDeviceFeatures;
using DeviceTelemetry.Infrastructure.DeviceLink;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace DeviceTelemetry.Api.Http;

/// <summary>
/// WebSocket endpoints: Device Link v1 for devices (<c>/api/device/v1/link</c>, device key, no IP limit) and the live
/// stream for the mobile app (<c>/api/mobile/devices/{id}/live</c>, temporarily public like the rest of the mobile API).
/// Neither is in the OpenAPI document.
/// </summary>
public static class DeviceLinkEndpoints
{
    public const string LinkRoute = "/api/device/v1/link";
    public const string LiveRoute = "/api/mobile/devices/{deviceId:int}/live";

    /// <summary>Retry-After of a 429 for too many live viewers.</summary>
    private const int ViewerLimitRetryAfterS = 30;

    public static IEndpointRouteBuilder MapDeviceLink(this IEndpointRouteBuilder app)
    {
        app.Map(LinkRoute, AcceptDeviceAsync)
            .AllowAnonymous()
            .ExcludeFromDescription();

        app.Map(LiveRoute, AcceptViewerAsync)
            .AllowAnonymous()
            .RequireRateLimiting(TelemetryRateLimiting.MobilePolicyName)
            .ExcludeFromDescription();

        return app;
    }

    private static async Task AcceptDeviceAsync(
        HttpContext context,
        IOptions<DeviceLinkOptions> options,
        IQueryHandler<AuthenticateDeviceQuery, int> authenticate,
        DeviceLinkHandler handler,
        IHostApplicationLifetime lifetime)
    {
        if (!options.Value.Enabled)
        {
            await WriteProblemAsync(context, StatusCodes.Status404NotFound, "feature.not_available", "Device Link is not enabled on this server.");
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            await WriteProblemAsync(context, StatusCodes.Status400BadRequest, "link.websocket_required", "Device Link requires a WebSocket upgrade.");
            return;
        }

        // Identity is checked before the upgrade, so a wrong key gets an HTTP 401 and never a WebSocket.
        var result = await authenticate.HandleAsync(new AuthenticateDeviceQuery
        {
            DeviceCode = context.Request.Headers[DeviceLinkProtocol.DeviceIdHeader].FirstOrDefault(),
            DeviceKey = context.Request.Headers[TelemetryController.DeviceKeyHeader].FirstOrDefault(),
        }, context.RequestAborted);

        if (result.IsFailure)
        {
            await WriteProblemAsync(context, StatusCodes.Status401Unauthorized, result.Error.Code, result.Error.Message);
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
        {
            KeepAliveInterval = options.Value.PingInterval,
            KeepAliveTimeout = options.Value.PongTimeout,
        });
        using var aborted = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lifetime.ApplicationStopping);

        await handler.RunAsync(socket, result.Value, aborted.Token);
    }

    private static async Task AcceptViewerAsync(
        int deviceId,
        HttpContext context,
        IQueryHandler<GetDeviceFeaturesQuery, IReadOnlyList<string>> features,
        LiveHub hub,
        IOptions<DeviceLinkOptions> options,
        IHostApplicationLifetime lifetime)
    {
        var result = await features.HandleAsync(new GetDeviceFeaturesQuery(deviceId), context.RequestAborted);
        if (result.IsFailure)
        {
            await WriteProblemAsync(context, StatusCodes.Status404NotFound, result.Error.Code, result.Error.Message);
            return;
        }

        if (!result.Value.Contains(MobileFeatures.Live))
        {
            var error = MobileErrors.FeatureNotAvailable;
            await WriteProblemAsync(context, StatusCodes.Status404NotFound, error.Code, error.Message);
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            await WriteProblemAsync(context, StatusCodes.Status400BadRequest, "live.websocket_required", "The live stream requires a WebSocket upgrade.");
            return;
        }

        var address = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        using var subscription = hub.TrySubscribe(deviceId, address, out _);
        if (subscription is null)
        {
            context.Response.Headers.RetryAfter = ViewerLimitRetryAfterS.ToString(CultureInfo.InvariantCulture);
            await WriteProblemAsync(context, StatusCodes.Status429TooManyRequests, "rate_limit.exceeded",
                "Too many live connections for this device or from this address.");
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
        {
            KeepAliveInterval = options.Value.PingInterval,
            KeepAliveTimeout = options.Value.PongTimeout,
        });
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lifetime.ApplicationStopping);

        hub.Activate(subscription);
        var receiving = ReceiveUntilClosedAsync(socket, stop);
        try
        {
            await foreach (var frame in subscription.ReadAllAsync(stop.Token))
                await socket.SendAsync(Encoding.UTF8.GetBytes(frame), WebSocketMessageType.Text, endOfMessage: true, stop.Token);

            // The frames end only when the device does not stream (status not_supported was the last frame).
            if (socket.State == WebSocketState.Open)
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "not supported", stop.Token);
            await Task.WhenAny(receiving, Task.Delay(TimeSpan.FromSeconds(5), stop.Token));
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
            // Client went away or server shutdown.
        }
        finally
        {
            await stop.CancelAsync();
            await receiving;
        }
    }

    /// <summary>Reads (and discards) client frames until the client closes; then cancels <paramref name="stop"/>.</summary>
    private static async Task ReceiveUntilClosedAsync(WebSocket socket, CancellationTokenSource stop)
    {
        var buffer = new byte[1024];
        try
        {
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer.AsMemory(), stop.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    if (socket.State == WebSocketState.CloseReceived)
                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
        }

        try
        {
            await stop.CancelAsync();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static Task WriteProblemAsync(HttpContext context, int status, string code, string detail)
    {
        context.Response.StatusCode = status;
        return context.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Detail = detail,
                Extensions = { ["code"] = code },
            },
        }).AsTask();
    }
}
