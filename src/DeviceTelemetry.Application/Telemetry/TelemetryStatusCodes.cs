namespace DeviceTelemetry.Application.Telemetry;

/// <summary>Status codes with a meaning on the server side.</summary>
public static class TelemetryStatusCodes
{
    /// <summary>Regular periodic report; carries the device's current values in <c>payload.values</c>. Never emailed.</summary>
    public const int RegularReport = 100010;

    /// <summary>Server event: the device has not reported for longer than its silence threshold.</summary>
    public const int DeviceSilent = 190001;

    /// <summary>Server event: a silent device reported again.</summary>
    public const int DeviceBackOnline = 190002;

    /// <summary>190000–199999 are raised by the server only; devices sending them are rejected.</summary>
    public static bool IsReservedForServer(int statusCode) => statusCode is >= 190_000 and <= 199_999;
}
