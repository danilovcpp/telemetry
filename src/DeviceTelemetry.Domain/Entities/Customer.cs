using DeviceTelemetry.Domain.Common;

namespace DeviceTelemetry.Domain.Entities;

public class Customer : BaseAuditableEntity
{
    public const int NameMaxLength = 200;

    /// <summary>RFC 5321 limit for a forward-path.</summary>
    public const int EmailMaxLength = 320;

    public int Id { get; set; }
    public required string Name { get; set; }

    /// <summary>Notification address. Optional: a customer without an address simply receives no emails.</summary>
    public string? Email { get; set; }

    /// <summary>Notification language (<c>en</c>, <c>ru</c>, <c>de</c>); used when the device sets none. <c>null</c>: the default language.</summary>
    public string? Language { get; set; }

    public ICollection<Device> Devices { get; } = [];
}
