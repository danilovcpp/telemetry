using System.Text.Json;
using DeviceTelemetry.Application.Mobile.Queries.GetCustomerDevices;
using DeviceTelemetry.Application.Mobile.Queries.GetDeviceDetails;
using DeviceTelemetry.Application.Mobile.Queries.GetDeviceEvents;

namespace DeviceTelemetry.UnitTests.Mobile;

public sealed class MobileContractTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static IEnumerable<string> JsonProperties<T>(T value)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(value, Web));
        return json.RootElement.EnumerateObject().Select(p => p.Name).ToList();
    }

    [Fact]
    public void Device_list_item_exposes_identity_and_liveness_only() =>
        Assert.Equal(
            ["id", "deviceCode", "name", "lastSeenAtUtc", "silentSinceUtc"],
            JsonProperties(new DeviceListItemDto(1, "uid", "Boiler", null, null)));

    [Fact]
    public void Device_details_expose_no_audit_or_secret_fields() =>
        Assert.Equal(
            ["id", "deviceCode", "name", "customerId", "lastEventAtUtc", "lastStatusCode", "lastSeenAtUtc", "reportIntervalS",
             "silentSinceUtc", "offlineAfterS", "monitoring", "timeZone", "channels", "features", "notificationsDisabled"],
            JsonProperties(new DeviceDetailsDto(1, "uid", "Boiler", 2, null, null, null, null, null, null, true, null, null, [], false)));

    [Fact]
    public void Device_details_never_expose_the_notify_email() =>
        Assert.DoesNotContain(typeof(DeviceDetailsDto).GetProperties(), p => p.Name.Contains("Email"));

    [Fact]
    public void Device_event_exposes_only_event_data() =>
        Assert.Equal(
            ["id", "statusCode", "occurredAtUtc", "receivedAtUtc", "payload", "source", "channels", "occurredAtRejected", "notification"],
            JsonProperties(new DeviceEventDto(Guid.NewGuid(), 1, DateTime.UtcNow, DateTime.UtcNow, null, "device", null, false, null)));

    [Fact]
    public void Event_notification_exposes_state_language_and_subject_but_no_address() =>
        Assert.Equal(
            ["status", "sentAtUtc", "language", "subject"],
            JsonProperties(new EventNotificationDto("sent", DateTime.UtcNow, "en", "Boiler: test")));

    [Fact]
    public void Payload_round_trips_as_raw_json()
    {
        var dto = new DeviceEventDto(Guid.NewGuid(), 1, DateTime.UtcNow, DateTime.UtcNow, """{"a":[1,2],"b":{"c":null}}""", "device", null, false, null);

        var json = JsonSerializer.Serialize(dto, Web);

        Assert.Contains("""
                        "payload":{"a":[1,2],"b":{"c":null}}
                        """, json);
        Assert.Equal(dto, JsonSerializer.Deserialize<DeviceEventDto>(json, Web));
    }

    [Fact]
    public void Cursor_round_trips_and_keeps_utc()
    {
        var cursor = new EventCursor(new DateTime(2026, 10, 16, 10, 30, 0, 123, DateTimeKind.Utc).AddTicks(4567), Guid.CreateVersion7());

        Assert.True(EventCursor.TryDecode(cursor.Encode(), out var decoded));
        Assert.Equal(cursor, decoded);
        Assert.Equal(DateTimeKind.Utc, decoded.OccurredAtUtc.Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("%%%")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")] // valid base64url, wrong length
    [InlineData("_____________________________________w")]   // right length, ticks out of range
    public void Malformed_cursor_is_rejected(string value) =>
        Assert.False(EventCursor.TryDecode(value, out _));
}
