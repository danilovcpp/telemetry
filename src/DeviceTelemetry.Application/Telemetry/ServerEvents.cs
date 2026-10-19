using System.Text.Json;
using DeviceTelemetry.Domain.Entities;

namespace DeviceTelemetry.Application.Telemetry;

/// <summary>Events the server raises on a device's behalf (status codes 19xxxx). They never carry a <c>messageId</c>.</summary>
public static class ServerEvents
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static TelemetryEvent Create(int deviceId, int statusCode, DateTimeOffset now, object details) => new()
    {
        Id = Guid.CreateVersion7(now),
        DeviceId = deviceId,
        StatusCode = statusCode,
        OccurredAtUtc = now.UtcDateTime,
        ReceivedAtUtc = now.UtcDateTime,
        Payload = JsonSerializer.Serialize(details, SerializerOptions),
        Source = TelemetryEventSource.Server,
    };
}
