namespace DeviceTelemetry.Application.Notifications;

/// <summary>
/// Languages notifications can be written in. The language of an email is the device's (<c>Device.NotifyLanguage</c>),
/// else the customer's (<c>Customer.Language</c>), else <see cref="Default"/>.
/// </summary>
public static class NotificationLanguages
{
    public const string English = "en";
    public const string Russian = "ru";
    public const string German = "de";

    public const string Default = English;

    public static IReadOnlyList<string> Supported { get; } = [English, Russian, German];

    /// <summary>
    /// Accepts a supported language code in any case, also with a region (<c>de-AT</c>, <c>ru_RU</c>),
    /// and returns the normalized two-letter code.
    /// </summary>
    public static bool TryNormalize(string? value, out string language)
    {
        language = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var primary = value.Trim().Split('-', '_')[0].ToLowerInvariant();
        if (!Supported.Contains(primary))
            return false;

        language = primary;
        return true;
    }
}
