using System.ComponentModel.DataAnnotations;

namespace DeviceTelemetry.Infrastructure.DeviceLink;

/// <summary>Device Link endpoint (section <c>DeviceLink</c>). Off by default.</summary>
public sealed class DeviceLinkOptions
{
    public const string SectionName = "DeviceLink";

    public bool Enabled { get; set; }

    /// <summary>The server sends a WebSocket ping this often.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:10:00")]
    public TimeSpan PingInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Time without a pong after a ping before the connection is dropped (30 s + 60 s = 90 s of silence).</summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:10:00")]
    public TimeSpan PongTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>The device must send <c>hello</c> within this time after the connection opens.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:10:00")]
    public TimeSpan HelloTimeout { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>Live values (section <c>Live</c>).</summary>
public sealed class LiveOptions
{
    public const string SectionName = "Live";

    /// <summary><c>periodMs</c> requested from the device in <c>live.start</c>.</summary>
    [Range(1000, 10_000)]
    public int PeriodMs { get; set; } = 1000;

    /// <summary>After the last viewer leaves, <c>live.stop</c> is sent this many seconds later (a quick reopen keeps the stream).</summary>
    [Range(0, 600)]
    public int StopDelayS { get; set; } = 10;

    /// <summary>
    /// <c>live.start</c> is repeated this often while there are viewers: the device holds a stream for 60 s (lease).
    /// Must stay well below 60.
    /// </summary>
    [Range(1, 50)]
    public int RenewIntervalS { get; set; } = 30;

    /// <summary>Retry delay after <c>busy</c> when the device gives no <c>retryAfterS</c>.</summary>
    [Range(1, 600)]
    public int BusyRetryS { get; set; } = 5;

    [Range(1, 1000)]
    public int MaxViewersPerDevice { get; set; } = 20;

    [Range(1, 1000)]
    public int MaxViewersPerIp { get; set; } = 4;
}

/// <summary>Copying data files from the device (section <c>Sync</c>).</summary>
public sealed class SyncOptions
{
    public const string SectionName = "Sync";

    /// <summary>Listings are refreshed this often while the device is connected.</summary>
    [Range(1, 86_400)]
    public int IntervalS { get; set; } = 300;

    /// <summary>Pause between two <c>file.read</c> requests of the background copy (not for files a viewer is waiting for).</summary>
    [Range(0, 60_000)]
    public int BackfillPauseMs { get; set; } = 250;

    /// <summary>Pause between background reads while a live stream of the device is running.</summary>
    [Range(0, 60_000)]
    public int LivePauseMs { get; set; } = 1000;

    /// <summary><c>length</c> of one <c>file.read</c>; the protocol allows at most 8192 (devices may return less).</summary>
    [Range(256, 8192)]
    public int ReadLength { get; set; } = 8192;

    /// <summary>A request without an answer within this time is abandoned.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:10:00")]
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Wait after <c>busy</c> or <c>card_error</c> when the device gives no <c>retryAfterS</c>.</summary>
    [Range(1, 3600)]
    public int RetryAfterDefaultS { get; set; } = 30;
}

/// <summary>Where the copies of device data files are kept (section <c>Storage</c>).</summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>Root directory; files live under <c>&lt;DataRoot&gt;/&lt;deviceId&gt;/&lt;device path&gt;</c>.</summary>
    [Required]
    public string DataRoot { get; set; } = "data";
}
