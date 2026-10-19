using DeviceTelemetry.Api.Contracts;
using DeviceTelemetry.Api.Http;
using DeviceTelemetry.Application.Common;
using DeviceTelemetry.Application.Devices.Commands.UpdateDeviceSettings;
using DeviceTelemetry.Application.Telemetry.Commands.SendTelemetry;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace DeviceTelemetry.Api.Controllers;

[ApiController]
[Route("api/telemetry")]
public class TelemetryController : ControllerBase
{
    /// <summary>Header carrying the per-device API key.</summary>
    public const string DeviceKeyHeader = "X-Device-Key";

    /// <summary>
    /// Accepts a telemetry message. Returns 202 when the event was stored (any email is only queued),
    /// or 200 with the original event when the same <c>messageId</c> was already accepted.
    /// </summary>
    [HttpPost("send")]
    [EnableRateLimiting(TelemetryRateLimiting.PolicyName)]
    [Consumes("application/json")]
    [ProducesResponseType<TelemetryAcceptedResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<TelemetryAcceptedResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Send(
        [FromBody] SendTelemetryRequest request,
        [FromHeader(Name = DeviceKeyHeader)] string? deviceKey,
        [FromServices] ICommandHandler<SendTelemetryCommand, SendTelemetryResult> handler)
    {
        var command = new SendTelemetryCommand
        {
            DeviceId = request.DeviceId!,
            DeviceKey = deviceKey,
            StatusCode = request.StatusCode!.Value,
            MessageId = request.MessageId,
            OccurredAtUtc = request.GetOccurredAtUtc(),
            Payload = request.GetPayloadJson(),
            NotifyEmail = request.NotifyEmail,
            NotifyLanguage = request.NotifyLanguage,
            Channels = request.GetChannels(),
            ReportIntervalS = request.ReportIntervalS,
        };

        var result = await handler.HandleAsync(command, HttpContext.RequestAborted);

        return result.Match<IActionResult>(
            accepted =>
            {
                var response = new TelemetryAcceptedResponse(
                    accepted.EventId, accepted.ReceivedAtUtc, accepted.IsDuplicate, accepted.NotificationQueued);
                return accepted.IsDuplicate ? Ok(response) : Accepted(response);
            },
            this.Problem);
    }

    /// <summary>
    /// The device reports its settings (name, notification address and language, time zone, report interval, offline
    /// threshold, monitoring, channel names). Returns 200 with the settings as stored and the fields that were rejected.
    /// Invalid values never fail the request. Counts as contact with the device.
    /// </summary>
    [HttpPost("settings")]
    [EnableRateLimiting(TelemetryRateLimiting.PolicyName)]
    [Consumes("application/json")]
    [ProducesResponseType<DeviceSettingsResult>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json")]
    public async Task<IActionResult> UpdateSettings(
        [FromBody] DeviceSettingsRequest request,
        [FromHeader(Name = DeviceKeyHeader)] string? deviceKey,
        [FromServices] ICommandHandler<UpdateDeviceSettingsCommand, DeviceSettingsResult> handler)
    {
        var command = new UpdateDeviceSettingsCommand
        {
            DeviceId = request.DeviceId!,
            DeviceKey = deviceKey,
            Settings = request.ToInput(),
        };

        var result = await handler.HandleAsync(command, HttpContext.RequestAborted);
        return result.Match<IActionResult>(Ok, this.Problem);
    }
}
