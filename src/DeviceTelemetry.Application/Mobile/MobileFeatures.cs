using System.Text.Json;

namespace DeviceTelemetry.Application.Mobile;

/// <summary>
/// Switches for the Device Link features (sections <c>DeviceLink</c> and <c>MobileApi</c>). Off by default: the mobile API
/// has no authentication yet, so live values and data files must not be exposed in production.
/// </summary>
public sealed class MobileFeatureOptions
{
    public bool DeviceLinkEnabled { get; set; }
    public bool LiveEnabled { get; set; }
    public bool HistoryEnabled { get; set; }
}

/// <summary>Computes the <c>features</c> array of the device card.</summary>
public static class MobileFeatures
{
    public const string Live = "live";
    public const string History = "history";

    /// <param name="linkHello">The last <c>hello</c> of the device (raw JSON), or <c>null</c>.</param>
    /// <param name="hasCopies">The server holds at least one copied data file of the device.</param>
    public static IReadOnlyList<string> For(MobileFeatureOptions options, string? linkHello, bool hasCopies)
    {
        var caps = Capabilities(linkHello);
        var features = new List<string>(2);

        if (options.DeviceLinkEnabled && options.LiveEnabled && caps.Contains(Live))
            features.Add(Live);

        if (options.HistoryEnabled && (hasCopies || (options.DeviceLinkEnabled && caps.Contains("files"))))
            features.Add(History);

        return features;
    }

    public static IReadOnlySet<string> Capabilities(string? linkHello)
    {
        if (string.IsNullOrEmpty(linkHello))
            return new HashSet<string>();

        try
        {
            using var document = JsonDocument.Parse(linkHello);
            if (document.RootElement.TryGetProperty("caps", out var caps) && caps.ValueKind == JsonValueKind.Array)
            {
                return caps.EnumerateArray()
                    .Where(c => c.ValueKind == JsonValueKind.String)
                    .Select(c => c.GetString()!)
                    .ToHashSet(StringComparer.Ordinal);
            }
        }
        catch (JsonException)
        {
            // A stored hello is always valid JSON; treat anything else as "no capabilities".
        }

        return new HashSet<string>();
    }
}
