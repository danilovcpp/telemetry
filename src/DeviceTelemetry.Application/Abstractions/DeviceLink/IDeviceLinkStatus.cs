namespace DeviceTelemetry.Application.Abstractions.DeviceLink;

/// <summary>What application code may know and ask about the Device Link connections (implemented in Infrastructure).</summary>
public interface IDeviceLinkStatus
{
    /// <summary>The device has a greeted Device Link connection to this server instance that can serve files.</summary>
    bool IsConnected(int deviceId);

    /// <summary>Puts a known file at the front of the device's copy queue (on-demand read). No-op when not connected.</summary>
    void RequestFileSync(int deviceId, string path);
}

/// <summary>Read access to the server copies of device data files.</summary>
public interface ILogFileStorage
{
    /// <summary>
    /// Opens the copy for reading, limited to <paramref name="length"/> bytes (what the metadata says is copied), or
    /// <c>null</c> when there is no file on disk.
    /// </summary>
    Stream? OpenRead(int deviceId, string path, long length);
}
