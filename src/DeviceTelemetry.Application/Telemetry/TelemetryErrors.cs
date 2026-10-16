using DeviceTelemetry.Application.Common;

namespace DeviceTelemetry.Application.Telemetry;

public static class TelemetryErrors
{
    /// <summary>
    /// Returned both for an unknown device and for a wrong or missing key, so callers cannot probe which device codes exist.
    /// The precise reason is logged server-side.
    /// </summary>
    public static readonly DomainError DeviceAuthenticationFailed = new(
        "device.authentication_failed",
        "The device is not registered or the device key is invalid.",
        DomainErrorType.Unauthorized);

    public static readonly DomainError MessageIdConflict = new(
        "telemetry.message_id_conflict",
        "A message with this messageId was already accepted from this device with different content.",
        DomainErrorType.Conflict);

    public static DomainError Validation(string details) => new("telemetry.invalid", details);
}
