using DeviceTelemetry.Application.Devices;
using DeviceTelemetry.Application.Devices.Commands.UpdateDeviceSettings;
using DeviceTelemetry.Application.Mobile.Queries.GetDeviceEvents;
using DeviceTelemetry.Application.Mobile.Queries.GetDeviceNotifications;
using DeviceTelemetry.Application.Outbox;
using DeviceTelemetry.Domain.Entities;
using DeviceTelemetry.Infrastructure.Persistence;
using DeviceTelemetry.Infrastructure.Persistence.Seeding;
using DeviceTelemetry.Tests.Shared;
using Microsoft.EntityFrameworkCore;

namespace DeviceTelemetry.IntegrationTests;

/// <summary>Email state in the mobile history (<c>notification</c>), the history filters and <c>/notifications</c>.</summary>
[Collection(PostgresCollection.Name)]
public sealed class EventNotificationTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 10, 27, 9, 0, 0, TimeSpan.Zero));
    private string _connectionString = string.Empty;

    public async Task InitializeAsync()
    {
        _connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using var db = NewContext();
        db.Templates.AddRange(DefaultTemplates.All.Select(DefaultTemplates.Copy));
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await using var db = NewContext();
        await db.Database.EnsureDeletedAsync();
    }

    private ApplicationDbContext NewContext() => PostgresFixture.CreateContext(_connectionString, _time);

    private async Task<Device> SeedDeviceAsync(string? customerEmail = "customer@example.com", string? customerLanguage = null)
    {
        await using var db = NewContext();
        return await TestData.SeedDeviceAsync(db, deviceName: "Logger", customerEmail: customerEmail, customerLanguage: customerLanguage);
    }

    private async Task SendAsync(Device device, int statusCode)
    {
        await using var db = NewContext();
        var result = await TestData.CreateHandler(db, _time).HandleAsync(TestData.Command(device.DeviceCode, statusCode), CancellationToken.None);
        Assert.True(result.IsSuccess);
        _time.Advance(TimeSpan.FromMinutes(1));
    }

    private async Task<List<DeviceEventDto>> EventsAsync(Device device, bool excludeReports = false, bool notified = false, int limit = 100)
    {
        await using var db = NewContext();
        var result = await new GetDeviceEventsQueryHandler(db).HandleAsync(
            new GetDeviceEventsQuery(device.Id, limit, null, excludeReports, notified), CancellationToken.None);
        return result.Value.Items.ToList();
    }

    [Fact]
    public async Task Each_event_tells_what_happened_to_its_email()
    {
        var device = await SeedDeviceAsync(customerLanguage: "ru");
        await SendAsync(device, 100001);   // template: queued
        await SendAsync(device, 100010);   // regular report: no template

        var events = await EventsAsync(device);

        var report = events.Single(e => e.StatusCode == 100010);
        Assert.Equal(new EventNotificationDto("no_template", null, null, null), report.Notification);

        var test = events.Single(e => e.StatusCode == 100001).Notification!;
        Assert.Equal(("queued", null, "ru"), (test.Status, test.SentAtUtc, test.Language));
        Assert.Contains("Logger", test.Subject);

        // The outbox worker delivered it.
        var sentAt = _time.UtcNow;
        await using (var db = NewContext())
        {
            await db.OutboxMessages.ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Status, OutboxMessageStatus.Sent)
                .SetProperty(m => m.ProcessedAtUtc, sentAt));
        }

        var sent = (await EventsAsync(device)).Single(e => e.StatusCode == 100001).Notification!;
        Assert.Equal(("sent", sentAt), (sent.Status, sent.SentAtUtc));
    }

    [Fact]
    public async Task Failed_delivery_switched_off_emails_and_missing_addresses_are_told_apart()
    {
        var failing = await SeedDeviceAsync();
        await SendAsync(failing, 100001);
        await using (var db = NewContext())
            await db.OutboxMessages.ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, OutboxMessageStatus.Failed));

        var noAddress = await SeedDeviceAsync(customerEmail: null);
        await SendAsync(noAddress, 100001);

        var switchedOff = await SeedDeviceAsync();
        await using (var db = NewContext())
        {
            await TestData.CreateSettingsHandler(db, _time).HandleAsync(
                new UpdateDeviceSettingsCommand
                {
                    DeviceId = switchedOff.DeviceCode, DeviceKey = TestData.DeviceKey, Settings = new DeviceSettingsInput(DisableNotifications: true),
                },
                CancellationToken.None);
        }
        await SendAsync(switchedOff, 100001);

        Assert.Equal("failed", (await EventsAsync(failing)).Single().Notification!.Status);
        Assert.Equal("no_address", (await EventsAsync(noAddress)).Single().Notification!.Status);
        Assert.Equal("disabled", (await EventsAsync(switchedOff)).Single(e => e.StatusCode == 100001).Notification!.Status);
    }

    [Fact]
    public async Task Events_stored_before_outcomes_were_recorded_have_no_notification()
    {
        var device = await SeedDeviceAsync();
        await using (var db = NewContext())
        {
            db.TelemetryEvents.Add(new TelemetryEvent
            {
                Id = Guid.CreateVersion7(), DeviceId = device.Id, StatusCode = 100001,
                OccurredAtUtc = _time.UtcNow, ReceivedAtUtc = _time.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        Assert.Null(Assert.Single(await EventsAsync(device)).Notification);
    }

    [Fact]
    public async Task History_can_skip_reports_or_keep_only_notified_events_across_pages()
    {
        var device = await SeedDeviceAsync();
        foreach (var code in new[] { 100010, 100001, 100010, 100003, 100010, 100010, 100000, 100010 })
            await SendAsync(device, code);

        Assert.Equal([100000, 100003, 100001], (await EventsAsync(device, excludeReports: true)).Select(e => e.StatusCode));
        Assert.Equal([100000, 100003, 100001], (await EventsAsync(device, notified: true)).Select(e => e.StatusCode));

        // Paging with a filter: two pages of two and one, no repeats.
        var codes = new List<int>();
        string? cursor = null;
        do
        {
            await using var db = NewContext();
            var page = (await new GetDeviceEventsQueryHandler(db).HandleAsync(
                new GetDeviceEventsQuery(device.Id, 2, cursor, Notified: true), CancellationToken.None)).Value;
            codes.AddRange(page.Items.Select(e => e.StatusCode));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        Assert.Equal([100000, 100003, 100001], codes);
    }

    [Fact]
    public async Task Notification_settings_list_language_codes_and_state()
    {
        var device = await SeedDeviceAsync(customerLanguage: "de");
        await using (var db = NewContext())
        {
            // An inactive German template of 100002 decides for German: no email, although the English one is active.
            var de = await db.Templates.SingleAsync(t => t.StatusCode == 100002 && t.Language == "de");
            de.IsActive = false;
            await db.SaveChangesAsync();
        }

        await using var query = NewContext();
        var settings = (await new GetDeviceNotificationsQueryHandler(query).HandleAsync(
            new GetDeviceNotificationsQuery(device.Id), CancellationToken.None)).Value;

        Assert.True(settings.Enabled);
        Assert.Null(settings.Reason);
        Assert.Equal("de", settings.Language);
        Assert.Contains(100001, settings.Codes);
        Assert.DoesNotContain(100002, settings.Codes);
        Assert.DoesNotContain(100010, settings.Codes);
        Assert.Equal(settings.Codes.Order(), settings.Codes);

        var noAddress = await SeedDeviceAsync(customerEmail: null);
        var missing = (await new GetDeviceNotificationsQueryHandler(query).HandleAsync(
            new GetDeviceNotificationsQuery(noAddress.Id), CancellationToken.None)).Value;
        Assert.Equal((false, "no_address"), (missing.Enabled, missing.Reason));

        var unknown = await new GetDeviceNotificationsQueryHandler(query).HandleAsync(
            new GetDeviceNotificationsQuery(int.MaxValue), CancellationToken.None);
        Assert.Equal("device.not_found", unknown.Error.Code);
    }
}
