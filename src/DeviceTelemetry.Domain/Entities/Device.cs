using DeviceTelemetry.Domain.Common;

namespace DeviceTelemetry.Domain.Entities;

public class Device : BaseAuditableEntity
{
    public const int NameMaxLength = 200;
    public const int DeviceCodeMaxLength = 100;
    public const int NotifyEmailMaxLength = 254;
    public const int MinReportIntervalS = 10;
    public const int MaxReportIntervalS = 86_400;

    public int Id { get; set; }
    public required string Name { get; set; }

    /// <summary>External, publicly known identifier the device reports as <c>deviceId</c>. Not a secret.</summary>
    public required string DeviceCode { get; set; }

    /// <summary>
    /// SHA-256 hash of the device API key. The plain key is never stored.
    /// <c>null</c> means no key has been issued and the device cannot send telemetry.
    /// </summary>
    public byte[]? ApiKeyHash { get; set; }

    /// <summary>Notification address requested by the device itself; overrides the customer's email when set.</summary>
    public string? NotifyEmail { get; set; }

    /// <summary>
    /// Period of the device's regular report in seconds, as last reported by the device. Only devices that report it
    /// are watched for silence: without an expected rhythm there is no meaningful threshold.
    /// </summary>
    public int? ReportIntervalS { get; set; }

    /// <summary>Server time of the last message accepted from the device (server-generated events do not count).</summary>
    public DateTime? LastSeenAtUtc { get; set; }

    /// <summary>
    /// When the server detected that the device stopped reporting and raised status 190001; <c>null</c> while it reports.
    /// Ensures one "silent" event per episode instead of one per check.
    /// </summary>
    public DateTime? SilentSinceUtc { get; set; }

    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;

    public ICollection<TelemetryEvent> Events { get; } = [];
}
