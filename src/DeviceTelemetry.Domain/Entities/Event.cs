using DeviceTelemetry.Domain.Common;

namespace DeviceTelemetry.Domain.Entities;

public class Event : BaseAuditableEntity
{
    public int Id { get; set; }
    public int DeviceId { get; set; }
    public string StatusCode { get; set; }
    public string Payload { get; set; }
}