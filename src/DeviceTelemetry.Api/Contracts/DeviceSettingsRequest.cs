using System.ComponentModel.DataAnnotations;
using DeviceTelemetry.Application.Devices;
using DeviceTelemetry.Application.Telemetry;

namespace DeviceTelemetry.Api.Contracts;

/// <summary>
/// Body of <c>POST /api/telemetry/settings</c>. Every field except <see cref="DeviceId"/> is optional: absent or <c>null</c>
/// keeps the stored value; an empty string clears it (not for <see cref="Name"/>); an empty <see cref="Channels"/> array
/// clears the catalog. Invalid values are reported in the response's <c>rejected</c> instead of failing the request.
/// </summary>
public sealed class DeviceSettingsRequest
{
    /// <summary>External device identifier (the device code).</summary>
    [Required]
    public string? DeviceId { get; init; }

    /// <summary>Display name of the device, used in emails and the mobile app (1–200 characters).</summary>
    public string? Name { get; init; }

    /// <summary>Address for this device's notifications; empty: use the customer's address.</summary>
    public string? NotifyEmail { get; init; }

    /// <summary>Notification language: <c>en</c>, <c>ru</c> or <c>de</c>; empty: the customer's language.</summary>
    public string? NotifyLanguage { get; init; }

    /// <summary>Device time zone for emails: IANA id (<c>Europe/Berlin</c>) or offset (<c>+02:00</c>); empty: UTC.</summary>
    public string? TimeZone { get; init; }

    /// <summary>Regular report period, 10–86400 seconds.</summary>
    public int? ReportIntervalS { get; init; }

    /// <summary>Consider the device disconnected after this many seconds without a message, 600–604800.</summary>
    public int? OfflineAfterS { get; init; }

    /// <summary><c>false</c>: planned shutdown, no "not reporting" alarms until set to <c>true</c> again.</summary>
    public bool? Monitoring { get; init; }

    /// <summary>Channel catalog: unique <c>id</c> (≤ 16) and <c>name</c> (≤ 32), at most 32 items. Replaces the stored list.</summary>
    public IReadOnlyList<ChannelRequest?>? Channels { get; init; }

    public DeviceSettingsInput ToInput() => new(
        NotifyEmail,
        NotifyLanguage,
        ReportIntervalS,
        Name,
        TimeZone,
        OfflineAfterS,
        Monitoring,
        Channels?.Select(c => c is null ? null! : new TelemetryChannel(c.Id ?? string.Empty, c.Name)).ToList());
}
