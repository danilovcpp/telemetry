using System.Text.Json;
using DeviceTelemetry.Application.Telemetry;
using DeviceTelemetry.Application.Telemetry.Commands.DetectSilentDevices;
using DeviceTelemetry.Domain.Entities;
using DeviceTelemetry.Infrastructure.Persistence;
using DeviceTelemetry.Infrastructure.Persistence.Seeding;
using DeviceTelemetry.Tests.Shared;
using Microsoft.EntityFrameworkCore;

namespace DeviceTelemetry.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class SilenceMonitorTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));
    private string _connectionString = string.Empty;
    private ApplicationDbContext _db = null!;

    public async Task InitializeAsync()
    {
        _connectionString = await postgres.CreateEmptyDatabaseAsync();
        _db = NewContext();
        _db.Templates.AddRange(DefaultTemplates.All.Select(DefaultTemplates.Copy));
        await _db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.Database.EnsureDeletedAsync();
        await _db.DisposeAsync();
    }

    private ApplicationDbContext NewContext() => PostgresFixture.CreateContext(_connectionString, _time);

    private async Task SendAsync(Device device, int statusCode = 100010, int? reportIntervalS = null)
    {
        await using var db = NewContext();
        var result = await TestData.CreateHandler(db, _time).HandleAsync(
            TestData.Command(device.DeviceCode, statusCode, reportIntervalS: reportIntervalS), CancellationToken.None);
        Assert.True(result.IsSuccess);
    }

    private async Task<int> CheckAsync()
    {
        await using var db = NewContext();
        return (await TestData.CreateSilenceHandler(db, _time).HandleAsync(new DetectSilentDevicesCommand(), CancellationToken.None)).Value;
    }

    private async Task<List<TelemetryEvent>> ServerEventsAsync(int deviceId)
    {
        await using var db = NewContext();
        return await db.TelemetryEvents
            .Where(e => e.DeviceId == deviceId && e.Source == TelemetryEventSource.Server)
            .OrderBy(e => e.Id)
            .ToListAsync();
    }

    [Fact]
    public async Task Short_interval_uses_the_ten_minute_floor_and_raises_exactly_one_silent_event()
    {
        var device = await TestData.SeedDeviceAsync(_db);
        await SendAsync(device, reportIntervalS: 60);

        _time.Advance(TimeSpan.FromMinutes(3)); // 3 × 60 s, but the floor is 10 minutes
        Assert.Equal(0, await CheckAsync());

        _time.Advance(TimeSpan.FromMinutes(8)); // 11 minutes of silence
        Assert.Equal(1, await CheckAsync());
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(0, await CheckAsync()); // same episode: no repeat

        var silent = Assert.Single(await ServerEventsAsync(device.Id));
        Assert.Equal(TelemetryStatusCodes.DeviceSilent, silent.StatusCode);
        Assert.Equal(_time.UtcNow.AddMinutes(-1), silent.OccurredAtUtc);
        using var details = JsonDocument.Parse(silent.Payload!);
        Assert.Equal(600, details.RootElement.GetProperty("thresholdS").GetInt32());
        Assert.NotNull((await ReloadAsync(device.Id)).SilentSinceUtc);
    }

    [Fact]
    public async Task Long_interval_uses_three_intervals()
    {
        var device = await TestData.SeedDeviceAsync(_db);
        await SendAsync(device, reportIntervalS: 600); // threshold 30 minutes

        _time.Advance(TimeSpan.FromMinutes(29));
        Assert.Equal(0, await CheckAsync());
        _time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(1, await CheckAsync());
    }

    [Fact]
    public async Task Next_message_ends_the_episode_with_exactly_one_back_online_event()
    {
        var device = await TestData.SeedDeviceAsync(_db);
        await SendAsync(device, reportIntervalS: 60);
        _time.Advance(TimeSpan.FromMinutes(11));
        await CheckAsync();

        _time.Advance(TimeSpan.FromMinutes(5));
        await SendAsync(device);
        await SendAsync(device);

        var serverEvents = await ServerEventsAsync(device.Id);
        Assert.Equal([TelemetryStatusCodes.DeviceSilent, TelemetryStatusCodes.DeviceBackOnline], serverEvents.Select(e => e.StatusCode));
        Assert.Null((await ReloadAsync(device.Id)).SilentSinceUtc);

        await using var db = NewContext();
        var emails = await db.OutboxMessages.Select(m => m.Content).ToListAsync();
        Assert.Equal(2, emails.Count); // 190001 and 190002; the 100010 reports are not emailed
    }

    [Fact]
    public async Task Device_without_report_interval_is_never_watched()
    {
        var device = await TestData.SeedDeviceAsync(_db);
        await SendAsync(device, statusCode: 100000);

        _time.Advance(TimeSpan.FromDays(1));

        Assert.Equal(0, await CheckAsync());
        Assert.Empty(await ServerEventsAsync(device.Id));
    }

    [Fact]
    public async Task Device_that_keeps_reporting_is_not_silent()
    {
        var device = await TestData.SeedDeviceAsync(_db);
        await SendAsync(device, reportIntervalS: 60);

        for (var i = 0; i < 30; i++)
        {
            _time.Advance(TimeSpan.FromMinutes(1));
            await SendAsync(device);
            Assert.Equal(0, await CheckAsync());
        }
    }

    [Fact]
    public async Task Concurrent_monitors_raise_one_silent_event()
    {
        var device = await TestData.SeedDeviceAsync(_db);
        await SendAsync(device, reportIntervalS: 60);
        _time.Advance(TimeSpan.FromMinutes(11));

        var marked = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(CheckAsync)));

        Assert.Equal(1, marked.Sum());
        Assert.Single(await ServerEventsAsync(device.Id));
    }

    [Fact]
    public async Task Silent_notification_goes_to_the_device_notify_email()
    {
        var device = await TestData.SeedDeviceAsync(_db);
        await using (var db = NewContext())
        {
            await TestData.CreateHandler(db, _time).HandleAsync(
                TestData.Command(device.DeviceCode, 100010, notifyEmail: "site-7@example.org", reportIntervalS: 60), CancellationToken.None);
        }

        _time.Advance(TimeSpan.FromMinutes(11));
        await CheckAsync();

        await using var verify = NewContext();
        var silentEventId = (await ServerEventsAsync(device.Id)).Single(e => e.StatusCode == 190001).Id;
        var email = (await verify.OutboxMessages.SingleAsync(m => m.TelemetryEventId == silentEventId)).ReadEmail();
        Assert.Equal("site-7@example.org", email.To);
        Assert.Equal("Boiler #1: device is not reporting", email.Subject);
    }

    private async Task<Device> ReloadAsync(int id)
    {
        await using var db = NewContext();
        return await db.Devices.AsNoTracking().SingleAsync(d => d.Id == id);
    }
}
