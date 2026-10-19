using System.Globalization;
using System.Text.RegularExpressions;

namespace DeviceTelemetry.Application.Devices;

/// <summary>
/// Device time zones: an IANA id (<c>Europe/Berlin</c>, preferred: it follows daylight saving time) or a fixed UTC offset
/// (<c>+02:00</c>, <c>-05:30</c>, <c>UTC</c>). IANA ids need the time zone database of the host (tzdata in the container).
/// </summary>
public static partial class DeviceTimeZones
{
    [GeneratedRegex(@"^(?:UTC|GMT)?\s*([+-])(\d{1,2})(?::?(\d{2}))?$", RegexOptions.IgnoreCase)]
    private static partial Regex OffsetPattern();

    /// <summary>Validates and normalizes: IANA ids keep their canonical spelling, offsets become <c>±HH:MM</c>.</summary>
    public static bool TryNormalize(string? value, out string timeZone)
    {
        timeZone = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        if (trimmed.Equals("UTC", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("GMT", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("Z", StringComparison.OrdinalIgnoreCase))
        {
            timeZone = "+00:00";
            return true;
        }

        if (TryParseOffset(trimmed, out var offset))
        {
            timeZone = FormatOffset(offset);
            return true;
        }

        // Only IANA ids ("Region/City"); Windows ids would not resolve inside the Linux container.
        if (trimmed.Contains('/') && TimeZoneInfo.TryFindSystemTimeZoneById(trimmed, out var zone) && zone.HasIanaId)
        {
            timeZone = zone.Id;
            return true;
        }

        return false;
    }

    /// <summary>
    /// The UTC time as local time of the zone, with a label such as <c>UTC+02:00, Europe/Berlin</c>.
    /// Without a (resolvable) zone the time stays UTC.
    /// </summary>
    public static (DateTime Local, string Label) ToLocal(DateTime utc, string? timeZone)
    {
        utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);

        if (timeZone is not null && TryParseOffset(timeZone, out var fixedOffset))
            return (utc + fixedOffset, "UTC" + FormatOffset(fixedOffset));

        if (timeZone is not null && TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out var zone))
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(utc, zone);
            return (local, $"UTC{FormatOffset(zone.GetUtcOffset(utc))}, {timeZone}");
        }

        return (utc, "UTC");
    }

    private static bool TryParseOffset(string value, out TimeSpan offset)
    {
        offset = TimeSpan.Zero;
        var match = OffsetPattern().Match(value);
        if (!match.Success)
            return false;

        var hours = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var minutes = match.Groups[3].Success ? int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
        if (hours > 14 || minutes > 59 || (hours == 14 && minutes > 0))
            return false;

        offset = new TimeSpan(hours, minutes, 0) * (match.Groups[1].Value == "-" ? -1 : 1);
        return true;
    }

    private static string FormatOffset(TimeSpan offset) =>
        (offset < TimeSpan.Zero ? "-" : "+") + offset.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture);
}
