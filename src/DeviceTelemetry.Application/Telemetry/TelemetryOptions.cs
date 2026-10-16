using System.ComponentModel.DataAnnotations;

namespace DeviceTelemetry.Application.Telemetry;

public sealed class TelemetryOptions
{
    public const string SectionName = "Telemetry";

    /// <summary>Maximum size of the serialized <c>payload</c> in UTF-8 bytes.</summary>
    [Range(1, 1_048_576)]
    public int MaxPayloadBytes { get; set; } = 8_192;

    /// <summary>How far in the future a device-reported <c>occurredAtUtc</c> may be, to tolerate clock drift.</summary>
    [Range(typeof(TimeSpan), "00:00:00", "1.00:00:00")]
    public TimeSpan MaxClockSkew { get; set; } = TimeSpan.FromMinutes(5);
}
