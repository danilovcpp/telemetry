using DeviceTelemetry.Domain.Common;

namespace DeviceTelemetry.Domain.Entities;

public class Device : BaseAuditableEntity
{
    public const int NameMaxLength = 200;
    public const int DeviceCodeMaxLength = 100;

    public int Id { get; set; }
    public required string Name { get; set; }

    /// <summary>External, publicly known identifier the device reports as <c>deviceId</c>. Not a secret.</summary>
    public required string DeviceCode { get; set; }

    /// <summary>
    /// SHA-256 hash of the device API key. The plain key is never stored.
    /// <c>null</c> means no key has been issued and the device cannot send telemetry.
    /// </summary>
    public byte[]? ApiKeyHash { get; set; }

    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;

    public ICollection<TelemetryEvent> Events { get; } = [];
}
