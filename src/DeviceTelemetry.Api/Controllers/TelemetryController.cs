using DeviceTelemetry.Application.Common;
using DeviceTelemetry.Application.Telemetry.Commands.SendTelemetry;
using Microsoft.AspNetCore.Mvc;

namespace DeviceTelemetry.Api.Controllers;

[ApiController]
[Route("api/telemetry")]
public class TelemetryController : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Send(
        [FromBody] SendTelemetryCommand command,
        [FromServices] ICommandHandler<SendTelemetryCommand> handler)
    {
        var result = await handler.HandleAsync(command, HttpContext.RequestAborted);
        return Ok(result.Value);
    }
}