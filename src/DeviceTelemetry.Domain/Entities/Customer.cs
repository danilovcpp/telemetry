using DeviceTelemetry.Domain.Common;

namespace DeviceTelemetry.Domain.Entities;

public class Customer : BaseAuditableEntity
{
    public int Id { get; set; }
    public string Name { get; set; }
    public string Email { get; set; }

    public ICollection<Device> Devices { get; } = [];
}