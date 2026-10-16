using DeviceTelemetry.Domain.Common;

namespace DeviceTelemetry.Domain.Entities;

public class Device : BaseAuditableEntity
{
    public int Id { get; set; }
    public string Name { get; set; }
    public string DeviceCode { get; set; }
    public int CustomerId { get; set; }

    public ICollection<Event> Events { get; } = [];
}