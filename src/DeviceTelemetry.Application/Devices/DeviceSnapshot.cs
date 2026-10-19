using System.Linq.Expressions;
using System.Text.Json;
using DeviceTelemetry.Application.Notifications;
using DeviceTelemetry.Application.Telemetry;
using DeviceTelemetry.Domain.Entities;

namespace DeviceTelemetry.Application.Devices;

/// <summary>The device and customer data needed to authenticate a device and address its notifications.</summary>
public sealed record DeviceSnapshot(
    int Id,
    string DeviceCode,
    string Name,
    byte[]? ApiKeyHash,
    string? NotifyEmail,
    string? NotifyLanguage,
    string? TimeZone,
    string? Channels,
    string CustomerName,
    string? CustomerEmail,
    string? CustomerLanguage)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Single-statement projection (device joined with its customer), translatable by EF.</summary>
    public static readonly Expression<Func<Device, DeviceSnapshot>> Projection = d => new DeviceSnapshot(
        d.Id, d.DeviceCode, d.Name, d.ApiKeyHash, d.NotifyEmail, d.NotifyLanguage, d.TimeZone, d.Channels,
        d.Customer.Name, d.Customer.Email, d.Customer.Language);

    /// <summary>The address notifications currently go to: the device's, else the customer's.</summary>
    public string? EffectiveEmail => NotifyEmail ?? CustomerEmail;

    /// <summary>The snapshot with a set of resolved settings applied (what the row looks like after the update).</summary>
    public DeviceSnapshot With(ResolvedDeviceSettings settings) => this with
    {
        Name = settings.Name.IsSet ? settings.Name.Value : Name,
        NotifyEmail = settings.NotifyEmail.IsSet ? settings.NotifyEmail.Value : NotifyEmail,
        NotifyLanguage = settings.NotifyLanguage.IsSet ? settings.NotifyLanguage.Value : NotifyLanguage,
        TimeZone = settings.TimeZone.IsSet ? settings.TimeZone.Value : TimeZone,
        Channels = settings.ChannelsJson.IsSet ? settings.ChannelsJson.Value : Channels,
    };

    public NotificationTarget ToTarget() => new(
        DeviceCode, Name, CustomerName, NotifyEmail, CustomerEmail, NotifyLanguage, CustomerLanguage, TimeZone,
        Channels is null ? null : JsonSerializer.Deserialize<List<TelemetryChannel>>(Channels, SerializerOptions));
}
