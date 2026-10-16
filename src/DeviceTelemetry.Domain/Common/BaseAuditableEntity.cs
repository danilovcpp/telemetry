namespace DeviceTelemetry.Domain.Common;

/// <summary>
/// Audit fields shared by all persisted entities. All timestamps are UTC.
/// The key is declared by each entity because key types differ (e.g. telemetry events use time-ordered GUIDs).
/// </summary>
public abstract class BaseAuditableEntity
{
    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
