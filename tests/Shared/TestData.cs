using DeviceTelemetry.Application.Devices;
using DeviceTelemetry.Application.Notifications;
using DeviceTelemetry.Application.Telemetry;
using DeviceTelemetry.Application.Telemetry.Commands.DetectSilentDevices;
using DeviceTelemetry.Application.Telemetry.Commands.SendTelemetry;
using DeviceTelemetry.Domain.Entities;
using DeviceTelemetry.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DeviceTelemetry.Tests.Shared;

public static class TestData
{
    public const string DeviceKey = "dtk_test-device-key";

    public static async Task<Device> SeedDeviceAsync(
        ApplicationDbContext db,
        string? deviceCode = null,
        string? customerEmail = "owner@example.com",
        string customerName = "ACME Corp",
        string deviceName = "Boiler #1")
    {
        var customer = new Customer { Name = customerName, Email = customerEmail };
        var device = new Device
        {
            Name = deviceName,
            DeviceCode = deviceCode ?? $"dev-{Guid.NewGuid():N}",
            ApiKeyHash = DeviceApiKey.Hash(DeviceKey),
            Customer = customer,
        };

        db.Devices.Add(device);
        await db.SaveChangesAsync();
        return device;
    }

    public static async Task<Template> SeedTemplateAsync(
        ApplicationDbContext db,
        int statusCode,
        string subject = "Device {DeviceName} reported {StatusCode}",
        string body = "Hello {CustomerName}, device {DeviceId} reported status {StatusCode} at {OccurredAtUtc}.",
        bool isActive = true,
        bool isHtml = false)
    {
        var template = new Template { StatusCode = statusCode, Subject = subject, Body = body, IsActive = isActive, IsHtml = isHtml };
        db.Templates.Add(template);
        await db.SaveChangesAsync();
        return template;
    }

    public static TelemetryNotifier CreateNotifier(ApplicationDbContext db, TimeProvider time) =>
        new(db, new EmailTemplateRenderer(), time, NullLogger<TelemetryNotifier>.Instance);

    public static SendTelemetryCommandHandler CreateHandler(ApplicationDbContext db, TimeProvider time) =>
        new(
            db,
            new SendTelemetryCommandValidator(Options.Create(new TelemetryOptions())),
            CreateNotifier(db, time),
            Options.Create(new TelemetryOptions()),
            time,
            NullLogger<SendTelemetryCommandHandler>.Instance);

    public static DetectSilentDevicesCommandHandler CreateSilenceHandler(
        ApplicationDbContext db, TimeProvider time, SilenceMonitorOptions? options = null) =>
        new(db, CreateNotifier(db, time), Options.Create(options ?? new SilenceMonitorOptions()), time,
            NullLogger<DetectSilentDevicesCommandHandler>.Instance);

    public static SendTelemetryCommand Command(
        string deviceCode,
        int statusCode = 100001,
        Guid? messageId = null,
        string? payload = null,
        DateTime? occurredAtUtc = null,
        string? deviceKey = DeviceKey,
        string? notifyEmail = null,
        IReadOnlyList<TelemetryChannel>? channels = null,
        int? reportIntervalS = null) => new()
    {
        DeviceId = deviceCode,
        DeviceKey = deviceKey,
        StatusCode = statusCode,
        MessageId = messageId,
        Payload = payload,
        OccurredAtUtc = occurredAtUtc,
        NotifyEmail = notifyEmail,
        Channels = channels,
        ReportIntervalS = reportIntervalS,
    };
}
