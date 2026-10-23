using System.Text.RegularExpressions;
using DeviceTelemetry.Domain.Entities;

namespace DeviceTelemetry.Infrastructure.DeviceLink;

/// <summary>Validation of file paths and months reported by a device, before they are stored or mapped to the disk.</summary>
public static partial class LogPaths
{
    // Relative or rooted at "/"; segments of letters, digits, underscore, hyphen and dot. No backslash, colon, percent or space.
    [GeneratedRegex(@"^/?[A-Za-z0-9_.\-]+(/[A-Za-z0-9_.\-]+)*$")]
    private static partial Regex Pattern();

    [GeneratedRegex(@"^\d{4}-(0[1-9]|1[0-2])$")]
    private static partial Regex MonthPattern();

    public static bool IsValid(string? path) =>
        path is { Length: > 0 and <= DeviceLogFile.PathMaxLength }
        && Pattern().IsMatch(path)
        && !path.Split('/').Any(segment => segment is "." or "..");

    public static bool IsValidMonth(string? month) => month is not null && MonthPattern().IsMatch(month);
}
