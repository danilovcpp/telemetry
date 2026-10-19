using DeviceTelemetry.Application.Common;
using DeviceTelemetry.Application.Telemetry;

namespace DeviceTelemetry.Application.Devices.Commands.UpdateDeviceSettings;

/// <summary>
/// A device reports its settings. Last write wins (device now, the mobile app later). Not a class with a generated
/// <c>ToString</c> for the same reason as the telemetry command: it carries the device key.
/// </summary>
public sealed class UpdateDeviceSettingsCommand : ICommand<DeviceSettingsResult>
{
    public required string DeviceId { get; init; }
    public string? DeviceKey { get; init; }
    public required DeviceSettingsInput Settings { get; init; }
}

/// <param name="Settings">The settings as stored after this update.</param>
/// <param name="Rejected">Fields that were ignored, with the reason; empty when everything was applied.</param>
public sealed record DeviceSettingsResult(DeviceSettingsDto Settings, IReadOnlyDictionary<string, string> Rejected);

/// <param name="Monitoring">Whether silence detection is active (it also needs a report interval or offlineAfterS).</param>
public sealed record DeviceSettingsDto(
    string Name,
    string? NotifyEmail,
    string? NotifyLanguage,
    string? TimeZone,
    int? ReportIntervalS,
    int? OfflineAfterS,
    bool Monitoring,
    IReadOnlyList<TelemetryChannel>? Channels);
