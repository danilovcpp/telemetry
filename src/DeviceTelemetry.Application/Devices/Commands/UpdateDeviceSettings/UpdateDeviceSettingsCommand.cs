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
/// <param name="NotifyEmail">
/// The address this device's emails go to now, after this update, whatever it comes from (the device's own address or the
/// customer's); <c>null</c> when no email is sent (switched off on the device, or no address anywhere). Always present.
/// </param>
public sealed record DeviceSettingsResult(
    DeviceSettingsDto Settings, IReadOnlyDictionary<string, string> Rejected, string? NotifyEmail);

/// <param name="NotifyEmail">The device's own address (empty: none). The address in use is <see cref="DeviceSettingsResult.NotifyEmail"/>.</param>
/// <param name="Monitoring">Whether silence detection is active (it also needs a report interval or offlineAfterS).</param>
/// <param name="NotificationsDisabled">The device switched emails off.</param>
public sealed record DeviceSettingsDto(
    string Name,
    string? NotifyEmail,
    string? NotifyLanguage,
    string? TimeZone,
    int? ReportIntervalS,
    int? OfflineAfterS,
    bool Monitoring,
    IReadOnlyList<TelemetryChannel>? Channels,
    bool NotificationsDisabled);
