using DeviceTelemetry.Domain.Common;

namespace DeviceTelemetry.Domain.Entities;

public class Template : BaseAuditableEntity
{
    public int Id { get; set; }
    public string StatusCode { get; set; }
    public string Message { get; set; }
}