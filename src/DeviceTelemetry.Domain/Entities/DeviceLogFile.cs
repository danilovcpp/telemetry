namespace DeviceTelemetry.Domain.Entities;

/// <summary>
/// Server copy of one daily data file of a device (Device Link, <c>logs.days</c>). The bytes live in file storage under
/// <c>&lt;deviceId&gt;/&lt;Path&gt;</c>; this row tracks what the device reported and how much has been copied.
/// </summary>
public class DeviceLogFile
{
    public const int PathMaxLength = 256;

    public long Id { get; set; }

    public int DeviceId { get; set; }
    public Device Device { get; set; } = null!;

    /// <summary>Channel number as in <c>logs.*</c> requests; 0 is the summary file of all channels.</summary>
    public int Channel { get; set; }

    /// <summary>Month the file belongs to, <c>YYYY-MM</c>.</summary>
    public required string Month { get; set; }

    /// <summary>Path as reported by the device (validated: relative, no <c>..</c>). Unique per device.</summary>
    public required string Path { get; set; }

    /// <summary>File size on the device at the last listing.</summary>
    public long DeviceSize { get; set; }

    /// <summary>Bytes copied to the server so far.</summary>
    public long StoredSize { get; set; }

    /// <summary>The device marked the day closed: the file no longer grows.</summary>
    public bool Closed { get; set; }

    /// <summary>The file disappeared from the device's listing; the copy is kept but not listed.</summary>
    public bool Deleted { get; set; }

    /// <summary>Incremented when the copy is discarded and re-read (the device file shrank); part of the ETag.</summary>
    public int Generation { get; set; }

    public DateTime? SyncedAtUtc { get; set; }

    /// <summary>The copy is final: the day is closed and every byte the device has was copied.</summary>
    public bool IsComplete => Closed && !Deleted && StoredSize == DeviceSize;
}
