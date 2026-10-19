using DeviceTelemetry.Application.Common;

namespace DeviceTelemetry.Application.Telemetry.Commands.DetectSilentDevices;

/// <summary>
/// Raises 190001 for every watched device that has been silent longer than its threshold.
/// Run periodically by the silence monitor; safe to run concurrently on several instances.
/// </summary>
public sealed record DetectSilentDevicesCommand : ICommand<int>;
