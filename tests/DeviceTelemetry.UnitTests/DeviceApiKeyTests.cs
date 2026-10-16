using DeviceTelemetry.Application.Devices;

namespace DeviceTelemetry.UnitTests;

public sealed class DeviceApiKeyTests
{
    [Fact]
    public void Generated_keys_are_unique_and_verify_against_their_hash()
    {
        var key = DeviceApiKey.Generate();
        var other = DeviceApiKey.Generate();

        Assert.StartsWith("dtk_", key);
        Assert.NotEqual(key, other);
        Assert.True(DeviceApiKey.Verify(key, DeviceApiKey.Hash(key)));
        Assert.False(DeviceApiKey.Verify(other, DeviceApiKey.Hash(key)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Missing_key_never_verifies(string? key) =>
        Assert.False(DeviceApiKey.Verify(key, DeviceApiKey.Hash("dtk_x")));

    [Fact]
    public void Device_without_issued_key_never_verifies() =>
        Assert.False(DeviceApiKey.Verify("dtk_x", storedHash: null));
}
