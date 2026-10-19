using DeviceTelemetry.Application.Common;
using DeviceTelemetry.Application.Devices;
using DeviceTelemetry.Application.Devices.Commands.UpdateDeviceSettings;
using DeviceTelemetry.Application.Telemetry;
using DeviceTelemetry.Application.Telemetry.Commands.DetectSilentDevices;
using DeviceTelemetry.Domain.Entities;
using DeviceTelemetry.Infrastructure.Persistence;
using DeviceTelemetry.Infrastructure.Persistence.Seeding;
using DeviceTelemetry.Tests.Shared;
using Microsoft.EntityFrameworkCore;

namespace DeviceTelemetry.IntegrationTests;

/// <summary><c>POST /api/telemetry/settings</c>: settings, address-change notice, offline threshold, monitoring, time zone.</summary>
[Collection(PostgresCollection.Name)]
public sealed class DeviceSettingsTests(PostgresFixture postgres) : IAsyncLifetime
{
    // 2026-10-16 is daylight saving time in Berlin (UTC+02:00).
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));
    private string _connectionString = string.Empty;
    private ApplicationDbContext _db = null!;
    private Device _device = null!;

    public async Task InitializeAsync()
    {
        _connectionString = await postgres.CreateEmptyDatabaseAsync();
        _db = NewContext();
        _db.Templates.AddRange(DefaultTemplates.All.Select(DefaultTemplates.Copy));
        await _db.SaveChangesAsync();
        _device = await TestData.SeedDeviceAsync(_db, deviceName: "Logger", customerEmail: "customer@example.com");
    }

    public async Task DisposeAsync()
    {
        await _db.Database.EnsureDeletedAsync();
        await _db.DisposeAsync();
    }

    private ApplicationDbContext NewContext() => PostgresFixture.CreateContext(_connectionString, _time);

    private async Task<Result<DeviceSettingsResult>> UpdateAsync(DeviceSettingsInput settings, string? key = TestData.DeviceKey)
    {
        await using var db = NewContext();
        var result = await TestData.CreateSettingsHandler(db, _time).HandleAsync(
            new UpdateDeviceSettingsCommand { DeviceId = _device.DeviceCode, DeviceKey = key, Settings = settings }, CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(1));
        return result;
    }

    private async Task SendAsync(int statusCode, IReadOnlyList<TelemetryChannel>? channels = null, string? notifyEmail = null)
    {
        await using var db = NewContext();
        var result = await TestData.CreateHandler(db, _time).HandleAsync(
            TestData.Command(_device.DeviceCode, statusCode, channels: channels, notifyEmail: notifyEmail), CancellationToken.None);
        Assert.True(result.IsSuccess);
        _time.Advance(TimeSpan.FromSeconds(1));
    }

    private async Task<int> CheckSilenceAsync()
    {
        await using var db = NewContext();
        return (await TestData.CreateSilenceHandler(db, _time).HandleAsync(new DetectSilentDevicesCommand(), CancellationToken.None)).Value;
    }

    private async Task<List<(int StatusCode, Application.Abstractions.Integrations.EmailMessage Email)>> EmailsAsync()
    {
        await using var db = NewContext();
        var rows = await db.OutboxMessages
            .Join(db.TelemetryEvents, m => m.TelemetryEventId, e => e.Id, (m, e) => new { e.StatusCode, Message = m })
            .OrderBy(x => x.Message.Id)
            .ToListAsync();
        return rows.Select(x => (x.StatusCode, x.Message.ReadEmail())).ToList();
    }

    [Fact]
    public async Task Settings_are_stored_and_returned()
    {
        var result = await UpdateAsync(new DeviceSettingsInput(
            NotifyEmail: "site@example.org", NotifyLanguage: "DE", ReportIntervalS: 300, Name: "  Boiler room logger ",
            TimeZone: "Europe/Berlin", OfflineAfterS: 1800, Monitoring: true,
            Channels: [new("ch1", "130AA15P20"), new("ch2", "TEX0000001")]));

        Assert.Empty(result.Value.Rejected);
        Assert.Equal(
            new DeviceSettingsDto("Boiler room logger", "site@example.org", "de", "Europe/Berlin", 300, 1800, true, result.Value.Settings.Channels),
            result.Value.Settings);
        Assert.Equal([new TelemetryChannel("ch1", "130AA15P20"), new TelemetryChannel("ch2", "TEX0000001")], result.Value.Settings.Channels!);
    }

    [Fact]
    public async Task Invalid_fields_are_rejected_individually_and_the_rest_is_applied()
    {
        var result = await UpdateAsync(new DeviceSettingsInput(
            NotifyEmail: "not an email", NotifyLanguage: "fr", ReportIntervalS: 5, Name: "", TimeZone: "Mars/Olympus",
            OfflineAfterS: 60, Monitoring: false, Channels: [new("ch1", "a"), new("ch1", "b")]));

        Assert.Equal(
            ["channels", "name", "notifyEmail", "notifyLanguage", "offlineAfterS", "reportIntervalS", "timeZone"],
            result.Value.Rejected.Keys.Order());
        Assert.Equal(("Logger", false, null), (result.Value.Settings.Name, result.Value.Settings.Monitoring, result.Value.Settings.NotifyEmail));
    }

    [Fact]
    public async Task Empty_strings_clear_and_absent_fields_keep()
    {
        await UpdateAsync(new DeviceSettingsInput(NotifyLanguage: "ru", TimeZone: "+03:00", Channels: [new("ch1", "x")]));

        var cleared = await UpdateAsync(new DeviceSettingsInput(NotifyLanguage: "", Channels: []));

        Assert.Equal((null, "+03:00", null), (cleared.Value.Settings.NotifyLanguage, cleared.Value.Settings.TimeZone, cleared.Value.Settings.Channels));
    }

    [Fact]
    public async Task Wrong_key_is_rejected_and_nothing_changes()
    {
        var result = await UpdateAsync(new DeviceSettingsInput(Name: "Hijacked"), key: "dtk_wrong");

        Assert.Equal(TelemetryErrors.DeviceAuthenticationFailed, result.Error);
        await using var db = NewContext();
        Assert.Equal("Logger", (await db.Devices.SingleAsync()).Name);
    }

    [Fact]
    public async Task Changing_the_address_notifies_the_previous_recipient_and_repeating_it_does_not()
    {
        await UpdateAsync(new DeviceSettingsInput(NotifyEmail: "site@example.org", NotifyLanguage: "ru"));
        await UpdateAsync(new DeviceSettingsInput(NotifyEmail: "site@example.org")); // same value: no notice
        await UpdateAsync(new DeviceSettingsInput(NotifyEmail: "other@example.org"));
        await UpdateAsync(new DeviceSettingsInput(NotifyEmail: "")); // back to the customer's address

        var notices = (await EmailsAsync()).Where(e => e.StatusCode == TelemetryStatusCodes.NotifyEmailChanged).Select(e => e.Email).ToList();
        Assert.Equal(["customer@example.com", "site@example.org", "other@example.org"], notices.Select(e => e.To));
        Assert.Contains("site@example.org", notices[0].Body);
        Assert.Contains("other@example.org", notices[1].Body);
        Assert.Contains("customer@example.com", notices[2].Body);
        Assert.Equal("Logger: изменён адрес уведомлений", notices[0].Subject);

        // Addresses never go into event payloads: those are readable through the public mobile API.
        await using var db = NewContext();
        Assert.All(await db.TelemetryEvents.Where(e => e.StatusCode == 190003).Select(e => e.Payload).ToListAsync(),
            payload => Assert.DoesNotContain("@", payload));
    }

    [Fact]
    public async Task Address_change_inside_a_telemetry_message_also_notifies_the_previous_recipient()
    {
        await SendAsync(100010, notifyEmail: "site@example.org");

        var notice = Assert.Single(await EmailsAsync(), e => e.StatusCode == TelemetryStatusCodes.NotifyEmailChanged);
        Assert.Equal("customer@example.com", notice.Email.To);
    }

    [Fact]
    public async Task Explicit_offline_threshold_replaces_the_derived_one()
    {
        await UpdateAsync(new DeviceSettingsInput(ReportIntervalS: 60, OfflineAfterS: 3600));

        _time.Advance(TimeSpan.FromMinutes(30)); // derived threshold (10 min) passed, explicit (60 min) not
        Assert.Equal(0, await CheckSilenceAsync());
        _time.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal(1, await CheckSilenceAsync());
    }

    [Fact]
    public async Task Offline_threshold_alone_enables_silence_detection()
    {
        await UpdateAsync(new DeviceSettingsInput(OfflineAfterS: 600));

        _time.Advance(TimeSpan.FromMinutes(11));

        Assert.Equal(1, await CheckSilenceAsync());
    }

    [Fact]
    public async Task Paused_monitoring_raises_no_silence_alarm_until_resumed()
    {
        await UpdateAsync(new DeviceSettingsInput(ReportIntervalS: 60, Monitoring: false));

        _time.Advance(TimeSpan.FromDays(2));
        Assert.Equal(0, await CheckSilenceAsync());

        await UpdateAsync(new DeviceSettingsInput(Monitoring: true));
        _time.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(1, await CheckSilenceAsync());
    }

    [Fact]
    public async Task Settings_command_counts_as_contact_and_ends_a_silence_episode()
    {
        await UpdateAsync(new DeviceSettingsInput(ReportIntervalS: 60));
        _time.Advance(TimeSpan.FromMinutes(11));
        await CheckSilenceAsync();

        await UpdateAsync(new DeviceSettingsInput());

        await using var db = NewContext();
        Assert.Equal([190001, 190002], (await db.TelemetryEvents.Select(e => e.StatusCode).ToListAsync()).Order());
        Assert.Null((await db.Devices.SingleAsync()).SilentSinceUtc);
    }

    [Fact]
    public async Task Channel_catalog_names_fill_in_channels_the_event_sends_without_names()
    {
        await UpdateAsync(new DeviceSettingsInput(NotifyLanguage: "en", Channels: [new("ch1", "130AA15P20"), new("ch2", "TEX0000001")]));

        await SendAsync(100003, channels: [new("ch2", null), new("ch7", null), new("ch1", "Override")]);

        var email = (await EmailsAsync()).Single(e => e.StatusCode == 100003).Email;
        Assert.Equal("Logger: channels not responding ch2 (TEX0000001), ch7, ch1 (Override)", email.Subject);
    }

    [Theory]
    [InlineData("Europe/Berlin", "Device time: 2026-10-16 14:00:00 (UTC+02:00, Europe/Berlin)")]
    [InlineData("+05:30", "Device time: 2026-10-16 17:30:00 (UTC+05:30)")]
    [InlineData(null, "Device time: 2026-10-16 12:00:00 (UTC)")]
    public async Task Emails_show_server_time_in_utc_and_device_time_in_its_zone(string? timeZone, string deviceLine)
    {
        if (timeZone is not null)
        {
            await UpdateAsync(new DeviceSettingsInput(TimeZone: timeZone));
            _time.Advance(TimeSpan.FromSeconds(-1)); // keep the event at exactly 12:00:00 UTC
        }

        await SendAsync(100001);

        var body = (await EmailsAsync()).Single(e => e.StatusCode == 100001).Email.Body;
        Assert.Contains("Server time (UTC): 2026-10-16 12:00:00\n" + deviceLine + "\n", body);
    }

    [Fact]
    public async Task Winter_time_uses_the_standard_offset()
    {
        _time.Advance(TimeSpan.FromDays(60)); // 2026-12-15: CET, UTC+01:00
        await UpdateAsync(new DeviceSettingsInput(TimeZone: "Europe/Berlin"));
        _time.Advance(TimeSpan.FromSeconds(-1));

        await SendAsync(100001);

        Assert.Contains("Device time: 2026-12-15 13:00:00 (UTC+01:00, Europe/Berlin)",
            (await EmailsAsync()).Single(e => e.StatusCode == 100001).Email.Body);
    }

    [Fact]
    public async Task Every_reference_template_renders_in_every_language()
    {
        foreach (var language in new[] { "en", "ru", "de" })
        {
            await UpdateAsync(new DeviceSettingsInput(NotifyLanguage: language, TimeZone: "Europe/Berlin"));
            foreach (var code in DefaultTemplates.StatusCodes.Where(c => c < 190_000))
                await SendAsync(code);
        }

        var emails = await EmailsAsync();
        Assert.Equal(3 * DefaultTemplates.StatusCodes.Count(c => c < 190_000), emails.Count(e => e.StatusCode < 190_000));
        Assert.All(emails, e => Assert.DoesNotContain("{", e.Email.Body));
    }
}
