using System.Net.Mail;
using System.Text.Json;
using DeviceTelemetry.Application.Notifications;
using DeviceTelemetry.Application.Telemetry;
using DeviceTelemetry.Domain.Entities;

namespace DeviceTelemetry.Application.Devices;

/// <summary>
/// Settings a device reports, either in the settings command or (the first three) in any telemetry message.
/// For every field: <c>null</c> = not sent, keep the stored value. Strings: empty = clear (except <see cref="Name"/>).
/// </summary>
public sealed record DeviceSettingsInput(
    string? NotifyEmail = null,
    string? NotifyLanguage = null,
    int? ReportIntervalS = null,
    string? Name = null,
    string? TimeZone = null,
    int? OfflineAfterS = null,
    bool? Monitoring = null,
    IReadOnlyList<TelemetryChannel>? Channels = null);

/// <summary>A resolved change of one setting: <see cref="IsSet"/> = write <see cref="Value"/> (a <c>null</c> value clears).</summary>
public readonly record struct SettingChange<T>(bool IsSet, T Value)
{
    public static readonly SettingChange<T> None = default;

    public static SettingChange<T> To(T value) => new(true, value);
}

/// <summary>
/// What to write after validating <see cref="DeviceSettingsInput"/>. Invalid values never fail the message: they are left
/// out of the changes and listed in <see cref="Rejected"/> (field name → reason, plus the raw value in <see cref="RejectedValues"/>).
/// </summary>
public sealed class ResolvedDeviceSettings
{
    public SettingChange<string?> NotifyEmail { get; init; }
    public SettingChange<string?> NotifyLanguage { get; init; }
    public SettingChange<int?> ReportIntervalS { get; init; }
    public SettingChange<string> Name { get; init; }
    public SettingChange<string?> TimeZone { get; init; }
    public SettingChange<int?> OfflineAfterS { get; init; }
    public SettingChange<bool?> Monitoring { get; init; }

    /// <summary>Normalized channel catalog as JSON (<c>[{"id","name"}]</c>); <c>null</c> value = clear.</summary>
    public SettingChange<string?> ChannelsJson { get; init; }

    public IReadOnlyDictionary<string, string> Rejected { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> RejectedValues { get; init; } = new Dictionary<string, string>();
}

public static class DeviceSettingsResolver
{
    private const int RejectedValueMaxLength = 254;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static ResolvedDeviceSettings Resolve(DeviceSettingsInput input)
    {
        var rejected = new Dictionary<string, string>();
        var rejectedValues = new Dictionary<string, string>();

        void Reject(string field, string reason, object raw)
        {
            rejected[field] = reason;
            var text = Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            rejectedValues[field] = text.Length <= RejectedValueMaxLength ? text : text[..RejectedValueMaxLength];
        }

        return new ResolvedDeviceSettings
        {
            NotifyEmail = ResolveEmail(input.NotifyEmail, Reject),
            NotifyLanguage = ResolveOptionalString(input.NotifyLanguage, "notifyLanguage",
                v => NotificationLanguages.TryNormalize(v, out var l) ? l : null,
                $"supported languages: {string.Join(", ", NotificationLanguages.Supported)}", Reject),
            TimeZone = ResolveOptionalString(input.TimeZone, "timeZone",
                v => DeviceTimeZones.TryNormalize(v, out var z) ? z : null,
                "expected an IANA time zone such as Europe/Berlin or an offset such as +02:00", Reject),
            ReportIntervalS = ResolveRange(input.ReportIntervalS, "reportIntervalS", Device.MinReportIntervalS, Device.MaxReportIntervalS, Reject),
            OfflineAfterS = ResolveRange(input.OfflineAfterS, "offlineAfterS", Device.MinOfflineAfterS, Device.MaxOfflineAfterS, Reject),
            Monitoring = input.Monitoring is { } monitoring ? SettingChange<bool?>.To(monitoring) : SettingChange<bool?>.None,
            Name = ResolveName(input.Name, Reject),
            ChannelsJson = ResolveChannels(input.Channels, Reject),
            Rejected = rejected,
            RejectedValues = rejectedValues,
        };
    }

    private static SettingChange<string?> ResolveEmail(string? sent, Action<string, string, object> reject)
    {
        if (sent is null)
            return SettingChange<string?>.None;

        var trimmed = sent.Trim();
        if (trimmed.Length == 0)
            return SettingChange<string?>.To(null);

        if (trimmed.Length <= Device.NotifyEmailMaxLength
            && MailAddress.TryCreate(trimmed, out var address)
            && string.Equals(address.Address, trimmed, StringComparison.Ordinal)) // a bare address, no display name
        {
            return SettingChange<string?>.To(address.Address);
        }

        reject("notifyEmail", "not a valid email address", trimmed);
        return SettingChange<string?>.None;
    }

    private static SettingChange<string?> ResolveOptionalString(
        string? sent, string field, Func<string, string?> normalize, string reason, Action<string, string, object> reject)
    {
        if (sent is null)
            return SettingChange<string?>.None;

        if (sent.Trim().Length == 0)
            return SettingChange<string?>.To(null);

        if (normalize(sent) is { } normalized)
            return SettingChange<string?>.To(normalized);

        reject(field, reason, sent.Trim());
        return SettingChange<string?>.None;
    }

    private static SettingChange<int?> ResolveRange(int? sent, string field, int min, int max, Action<string, string, object> reject)
    {
        if (sent is null)
            return SettingChange<int?>.None;

        if (sent >= min && sent <= max)
            return SettingChange<int?>.To(sent);

        reject(field, $"allowed range is {min}..{max}", sent.Value);
        return SettingChange<int?>.None;
    }

    private static SettingChange<string> ResolveName(string? sent, Action<string, string, object> reject)
    {
        if (sent is null)
            return SettingChange<string>.None;

        var trimmed = sent.Trim();
        if (trimmed.Length is > 0 and <= Device.NameMaxLength)
            return SettingChange<string>.To(trimmed);

        reject("name", $"must be 1..{Device.NameMaxLength} characters", trimmed);
        return SettingChange<string>.None;
    }

    private static SettingChange<string?> ResolveChannels(IReadOnlyList<TelemetryChannel>? sent, Action<string, string, object> reject)
    {
        if (sent is null)
            return SettingChange<string?>.None;

        if (sent.Count == 0)
            return SettingChange<string?>.To(null);

        var valid = sent.Count <= TelemetryChannel.MaxCount
                    && sent.All(c => c is not null
                                     && !string.IsNullOrWhiteSpace(c.Id) && c.Id.Length <= TelemetryChannel.IdMaxLength
                                     && (c.Name is null || c.Name.Length <= TelemetryChannel.NameMaxLength))
                    && sent.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() == sent.Count;
        if (!valid)
        {
            reject("channels",
                $"at most {TelemetryChannel.MaxCount} items with unique 'id' (1..{TelemetryChannel.IdMaxLength} characters) and 'name' up to {TelemetryChannel.NameMaxLength}",
                $"{sent.Count} item(s)");
            return SettingChange<string?>.None;
        }

        return SettingChange<string?>.To(JsonSerializer.Serialize(sent, SerializerOptions));
    }
}
