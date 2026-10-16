using DeviceTelemetry.Application.Common;

namespace DeviceTelemetry.Application.Telemetry.Commands.SendTelemetry;

public class SendTelemetryCommand : ICommand
{
    public string DeviceCode { get; set; }
    public string Status { get; set; }
}