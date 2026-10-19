using System.ComponentModel.DataAnnotations;

namespace DeviceTelemetry.Application.Telemetry;

/// <summary>Detection of devices that stopped reporting (section <c>SilenceMonitor</c>).</summary>
public sealed class SilenceMonitorOptions
{
    public const string SectionName = "SilenceMonitor";

    /// <summary>How often devices are checked.</summary>
    [Range(typeof(TimeSpan), "00:00:05", "01:00:00")]
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>A device is silent after this many report intervals without any message…</summary>
    [Range(2, 100)]
    public int ThresholdIntervals { get; set; } = 3;

    /// <summary>…but never sooner than this, so short intervals do not raise alarms on brief network hiccups.</summary>
    [Range(typeof(TimeSpan), "00:01:00", "1.00:00:00")]
    public TimeSpan MinThreshold { get; set; } = TimeSpan.FromMinutes(10);

    public TimeSpan ThresholdFor(int reportIntervalS)
    {
        var threshold = TimeSpan.FromSeconds((long)reportIntervalS * ThresholdIntervals);
        return threshold > MinThreshold ? threshold : MinThreshold;
    }
}
