using System.Collections.Concurrent;
using DeviceTelemetry.Application.Abstractions.DeviceLink;
using Microsoft.Extensions.Logging;

namespace DeviceTelemetry.Infrastructure.DeviceLink;

/// <summary>The greeted Device Link connections of this server instance: at most one per device.</summary>
public sealed class DeviceLinkRegistry(ILogger<DeviceLinkRegistry> logger) : IDeviceLinkStatus
{
    private readonly ConcurrentDictionary<int, DeviceLinkSession> _sessions = new();

    public DeviceLinkSession? Find(int deviceId) => _sessions.GetValueOrDefault(deviceId);

    /// <summary>Makes <paramref name="session"/> the device's connection; an older one is closed with 4409.</summary>
    public async Task RegisterAsync(DeviceLinkSession session)
    {
        DeviceLinkSession? replaced = null;
        _sessions.AddOrUpdate(
            session.DeviceId,
            session,
            (_, existing) =>
            {
                replaced = existing;
                return session;
            });

        if (replaced is not null && !ReferenceEquals(replaced, session))
        {
            logger.LogInformation(
                "Device link of device {DeviceId}: connection {OldConnection} replaced by {NewConnection}",
                session.DeviceId, replaced.ConnectionId, session.ConnectionId);
            await replaced.CloseAsync(DeviceLinkProtocol.Replaced, "replaced by a newer connection");
        }
    }

    /// <summary>Removes the session if it is still the device's current connection.</summary>
    public bool Remove(DeviceLinkSession session) =>
        _sessions.TryRemove(KeyValuePair.Create(session.DeviceId, session));

    /// <summary>Connected with the <c>files</c> capability, i.e. a missing copy can be fetched now.</summary>
    public bool IsConnected(int deviceId) => Find(deviceId)?.Hello.Supports(LiveHub.FilesCapability) == true;

    public void RequestFileSync(int deviceId, string path) => Find(deviceId)?.RequestFileSync(path);
}
