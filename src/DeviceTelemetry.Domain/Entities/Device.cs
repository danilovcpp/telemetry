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
    /// The device switched email notifications off (<c>"notifyEmail": null</c> in the settings command). No email is sent for
    /// this device to any address until the device sends an address again; events, reports and silence detection go on.
    /// </summary>
    public bool NotificationsDisabled { get; set; }

    /// <summary>Notification language requested by the device; overrides the customer's language when set.</summary>
    public string? NotifyLanguage { get; set; }

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

    /// <summary>
    /// Explicit silence threshold set by the device (600–604 800 s). When set it replaces the derived
    /// <c>max(3 × ReportIntervalS, 10 min)</c>, and the device is watched even without a report interval.
    /// </summary>
    public int? OfflineAfterS { get; set; }

    /// <summary><c>false</c> pauses silence detection (planned shutdown). <c>null</c> or <c>true</c>: watched.</summary>
    public bool? MonitoringEnabled { get; set; }

    /// <summary>Device time zone, an IANA id (<c>Europe/Berlin</c>) or a fixed offset (<c>+02:00</c>); shown in emails.</summary>
    public string? TimeZone { get; set; }

    /// <summary>Channel catalog reported by the device: JSON array of <c>{"id", "name"}</c> (<c>jsonb</c>).</summary>
    public string? Channels { get; set; }

    /// <summary>
    /// Last <c>hello</c> the device sent over Device Link (<c>jsonb</c>, as received): link version, schema, firmware,
    /// capabilities (<c>caps</c>), channels. <c>null</c> if the device never connected over the link.
    /// </summary>
    public string? LinkHello { get; set; }

    public DateTime? LinkHelloAtUtc { get; set; }

    public ICollection<DeviceLogFile> LogFiles { get; } = [];

    public const int MinOfflineAfterS = 600;
    public const int MaxOfflineAfterS = 604_800;
    public const int TimeZoneMaxLength = 64;

    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;

    public ICollection<TelemetryEvent> Events { get; } = [];
}
