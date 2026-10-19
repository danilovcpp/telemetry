using DeviceTelemetry.Application.Common;
using DeviceTelemetry.Application.Telemetry.Commands.DetectSilentDevices;
using DeviceTelemetry.Application.Telemetry.Commands.SendTelemetry;
using DeviceTelemetry.Domain.Entities;
using DeviceTelemetry.Infrastructure.Persistence;
using DeviceTelemetry.Infrastructure.Persistence.Seeding;
using DeviceTelemetry.Tests.Shared;
using Microsoft.EntityFrameworkCore;

namespace DeviceTelemetry.IntegrationTests;

/// <summary>Language of notifications: device's, else customer's, else English; template fallback rules.</summary>
[Collection(PostgresCollection.Name)]
public sealed class NotificationLanguageTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const int CheckCode = 100001; // "connectivity check" in the reference set

    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));
    private string _connectionString = string.Empty;
    private ApplicationDbContext _db = null!;

    public async Task InitializeAsync()
    {
        _connectionString = await postgres.CreateEmptyDatabaseAsync();
        _db = NewContext();
    }

    public async Task DisposeAsync()
    {
        await _db.Database.EnsureDeletedAsync();
        await _db.DisposeAsync();
    }

    private ApplicationDbContext NewContext() => PostgresFixture.CreateContext(_connectionString, _time);

    private async Task SeedReferenceTemplatesAsync()
    {
        _db.Templates.AddRange(DefaultTemplates.All.Select(DefaultTemplates.Copy));
        await _db.SaveChangesAsync();
    }

    private async Task<Result<SendTelemetryResult>> SendAsync(SendTelemetryCommand command)
    {
        await using var db = NewContext();
        var result = await TestData.CreateHandler(db, _time).HandleAsync(command, CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(1)); // keeps time-ordered outbox ids in send order
        return result;
    }

    private async Task<string> LastSubjectAsync()
    {
        await using var db = NewContext();
        return (await db.OutboxMessages.OrderByDescending(m => m.Id).FirstAsync()).ReadEmail().Subject;
    }

    [Fact]
    public async Task Without_any_language_the_email_is_english()
    {
        var device = await TestData.SeedDeviceAsync(_db, deviceName: "Logger");
        await SeedReferenceTemplatesAsync();

        await SendAsync(TestData.Command(device.DeviceCode, CheckCode));

        Assert.Equal("Logger: connectivity check — message received", await LastSubjectAsync());
    }

    [Theory]
    [InlineData("ru", "Logger: проверка связи — сообщение получено")]
    [InlineData("de", "Logger: Verbindungstest – Nachricht empfangen")]
    [InlineData("en", "Logger: connectivity check — message received")]
    public async Task Customer_language_is_used_when_the_device_sets_none(string language, string subject)
    {
        var device = await TestData.SeedDeviceAsync(_db, deviceName: "Logger", customerLanguage: language);
        await SeedReferenceTemplatesAsync();

        await SendAsync(TestData.Command(device.DeviceCode, CheckCode));

        Assert.Equal(subject, await LastSubjectAsync());
    }

    [Fact]
    public async Task Device_language_wins_over_the_customer_language_until_it_is_cleared()
    {
        var device = await TestData.SeedDeviceAsync(_db, deviceName: "Logger", customerLanguage: "ru");
        await SeedReferenceTemplatesAsync();

        await SendAsync(TestData.Command(device.DeviceCode, CheckCode, notifyLanguage: "DE-at"));
        Assert.Equal("Logger: Verbindungstest – Nachricht empfangen", await LastSubjectAsync());

        await SendAsync(TestData.Command(device.DeviceCode, CheckCode)); // field absent: kept
        Assert.Equal("Logger: Verbindungstest – Nachricht empfangen", await LastSubjectAsync());
        Assert.Equal("de", (await ReloadDeviceAsync(device.Id)).NotifyLanguage);

        await SendAsync(TestData.Command(device.DeviceCode, CheckCode, notifyLanguage: "")); // cleared: customer's again
        Assert.Equal("Logger: проверка связи — сообщение получено", await LastSubjectAsync());
        Assert.Null((await ReloadDeviceAsync(device.Id)).NotifyLanguage);
    }

    [Theory]
    [InlineData("fr")]
    [InlineData("english")]
    [InlineData("12")]
    public async Task Unsupported_device_language_is_ignored_and_recorded_but_the_email_goes_out(string language)
    {
        var device = await TestData.SeedDeviceAsync(_db, deviceName: "Logger", customerLanguage: "ru");
        await SeedReferenceTemplatesAsync();

        var result = await SendAsync(TestData.Command(device.DeviceCode, CheckCode, notifyLanguage: language));

        Assert.True(result.Value.NotificationQueued);
        Assert.Equal("Logger: проверка связи — сообщение получено", await LastSubjectAsync());
        Assert.Null((await ReloadDeviceAsync(device.Id)).NotifyLanguage);
        await using var db = NewContext();
        Assert.Equal(language, (await db.TelemetryEvents.SingleAsync()).RejectedNotifyLanguage);
    }

    [Fact]
    public async Task Missing_translation_falls_back_to_english_then_to_the_language_neutral_template()
    {
        var device = await TestData.SeedDeviceAsync(_db, customerLanguage: "de");
        await TestData.SeedTemplateAsync(_db, 500, subject: "neutral", language: null);
        await TestData.SeedTemplateAsync(_db, 600, subject: "english", language: "en");
        await TestData.SeedTemplateAsync(_db, 600, subject: "neutral 600", language: null);

        await SendAsync(TestData.Command(device.DeviceCode, 500));
        Assert.Equal("neutral", await LastSubjectAsync());

        await SendAsync(TestData.Command(device.DeviceCode, 600));
        Assert.Equal("english", await LastSubjectAsync());
    }

    [Fact]
    public async Task Deactivated_translation_stops_the_email_instead_of_falling_back()
    {
        var device = await TestData.SeedDeviceAsync(_db, customerLanguage: "ru");
        await TestData.SeedTemplateAsync(_db, 600, subject: "english", language: "en");
        await TestData.SeedTemplateAsync(_db, 600, subject: "russian", language: "ru", isActive: false);

        var result = await SendAsync(TestData.Command(device.DeviceCode, 600));

        Assert.False(result.Value.NotificationQueued);
    }

    [Fact]
    public async Task Notify_language_is_not_part_of_the_idempotency_hash()
    {
        var device = await TestData.SeedDeviceAsync(_db);
        var messageId = Guid.NewGuid();

        var first = await SendAsync(TestData.Command(device.DeviceCode, 7, messageId));
        var retry = await SendAsync(TestData.Command(device.DeviceCode, 7, messageId, notifyLanguage: "ru"));

        Assert.True(retry.Value.IsDuplicate);
        Assert.Equal(first.Value.EventId, retry.Value.EventId);
    }

    [Fact]
    public async Task Silence_email_uses_the_device_language()
    {
        var device = await TestData.SeedDeviceAsync(_db, deviceName: "Logger");
        await SeedReferenceTemplatesAsync();
        await SendAsync(TestData.Command(device.DeviceCode, 100010, notifyLanguage: "ru", reportIntervalS: 60));

        _time.Advance(TimeSpan.FromMinutes(11));
        await using (var db = NewContext())
            await TestData.CreateSilenceHandler(db, _time).HandleAsync(new DetectSilentDevicesCommand(), CancellationToken.None);

        Assert.Equal("Logger: прибор не выходит на связь", await LastSubjectAsync());
    }

    [Fact]
    public async Task Reference_set_covers_every_code_in_every_language_and_never_100010()
    {
        Assert.Equal(DefaultTemplates.StatusCodes.Count * 3, DefaultTemplates.All.Count);
        Assert.All(new[] { "en", "ru", "de" }, language =>
            Assert.Equal(DefaultTemplates.StatusCodes, DefaultTemplates.All.Where(t => t.Language == language).Select(t => t.StatusCode)));
        Assert.DoesNotContain(DefaultTemplates.All, t => t.StatusCode == 100010);

        // Every reference template renders without errors, including {Channels} without a channel list.
        var device = await TestData.SeedDeviceAsync(_db);
        await SeedReferenceTemplatesAsync();
        foreach (var language in new[] { "en", "ru", "de" })
        {
            foreach (var code in DefaultTemplates.StatusCodes.Where(c => c < 190_000))
            {
                var result = await SendAsync(TestData.Command(device.DeviceCode, code, notifyLanguage: language));
                Assert.True(result.Value.NotificationQueued, $"{code}/{language} was not queued");
            }
        }
    }

    private async Task<Device> ReloadDeviceAsync(int id)
    {
        await using var db = NewContext();
        return await db.Devices.AsNoTracking().SingleAsync(d => d.Id == id);
    }
}
