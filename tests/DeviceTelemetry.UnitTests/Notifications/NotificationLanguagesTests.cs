using DeviceTelemetry.Application.Notifications;

namespace DeviceTelemetry.UnitTests.Notifications;

public sealed class NotificationLanguagesTests
{
    [Theory]
    [InlineData("en", "en")]
    [InlineData("RU", "ru")]
    [InlineData(" de ", "de")]
    [InlineData("de-AT", "de")]
    [InlineData("ru_RU", "ru")]
    public void Supported_codes_are_normalized(string input, string expected)
    {
        Assert.True(NotificationLanguages.TryNormalize(input, out var language));
        Assert.Equal(expected, language);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("fr")]
    [InlineData("english")]
    [InlineData("-")]
    public void Unsupported_values_are_rejected(string? input) =>
        Assert.False(NotificationLanguages.TryNormalize(input, out _));

    [Fact]
    public void Default_is_english_and_supported() =>
        Assert.Equal(("en", true), (NotificationLanguages.Default, NotificationLanguages.Supported.Contains(NotificationLanguages.Default)));
}
