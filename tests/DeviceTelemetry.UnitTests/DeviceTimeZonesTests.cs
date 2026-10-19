using DeviceTelemetry.Application.Devices;

namespace DeviceTelemetry.UnitTests;

public sealed class DeviceTimeZonesTests
{
    [Theory]
    [InlineData("+02:00", "+02:00")]
    [InlineData("-05:30", "-05:30")]
    [InlineData("+3", "+03:00")]
    [InlineData("UTC+0100", "+01:00")]
    [InlineData("utc", "+00:00")]
    [InlineData("Europe/Berlin", "Europe/Berlin")]
    [InlineData(" Asia/Kolkata ", "Asia/Kolkata")]
    public void Valid_zones_are_normalized(string input, string expected)
    {
        Assert.True(DeviceTimeZones.TryNormalize(input, out var zone));
        Assert.Equal(expected, zone);
    }

    [Theory]
    [InlineData("Mars/Olympus")]
    [InlineData("W. Europe Standard Time")] // Windows id: would not resolve in the Linux container
    [InlineData("+15:00")]
    [InlineData("+02:75")]
    [InlineData("berlin")]
    [InlineData("")]
    public void Invalid_zones_are_rejected(string input) =>
        Assert.False(DeviceTimeZones.TryNormalize(input, out _));

    [Fact]
    public void Iana_zone_follows_daylight_saving_time()
    {
        var summer = DeviceTimeZones.ToLocal(new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc), "Europe/Berlin");
        var winter = DeviceTimeZones.ToLocal(new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc), "Europe/Berlin");

        Assert.Equal((new DateTime(2026, 7, 1, 14, 0, 0), "UTC+02:00, Europe/Berlin"), (summer.Local, summer.Label));
        Assert.Equal((new DateTime(2026, 1, 1, 13, 0, 0), "UTC+01:00, Europe/Berlin"), (winter.Local, winter.Label));
    }

    [Fact]
    public void Missing_zone_keeps_utc() =>
        Assert.Equal("UTC", DeviceTimeZones.ToLocal(DateTime.UtcNow, null).Label);
}
