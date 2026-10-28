namespace DeviceTelemetry.Domain.Entities;

/// <summary>
/// A point in the history of a device's lifetime counters (<c>payload.lifetime</c>): stored when a counter other than the
/// powered minutes changed, so a report that only adds powered time leaves no row.
/// </summary>
public class DeviceLifetimeSnapshot
{
    public long Id { get; set; }

    public int DeviceId { get; set; }
    public Device Device { get; set; } = null!;

    /// <summary>The event the counters came from (unique: one point per event).</summary>
    public Guid EventId { get; set; }

    /// <summary><c>OccurredAtUtc</c> of that event.</summary>
    public DateTime OccurredAtUtc { get; set; }

    /// <summary>The <c>lifetime</c> object as the device sent it (<c>jsonb</c>).</summary>
    public required string Lifetime { get; set; }
}
