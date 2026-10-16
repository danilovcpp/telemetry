using DeviceTelemetry.Api.Contracts;
using DeviceTelemetry.Api.Http;
using DeviceTelemetry.Application.Common;
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
}
