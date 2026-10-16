namespace DeviceTelemetry.Tests.Shared;

/// <summary>A <see cref="TimeProvider"/> whose clock only moves when the test says so.</summary>
public sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private DateTimeOffset _utcNow = utcNow;

    public DateTime UtcNow => _utcNow.UtcDateTime;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan by) => _utcNow += by;
}
