using System.Text.Json.Nodes;
using DeviceTelemetry.Application.Devices;
using DeviceTelemetry.Application.Mobile.Queries.GetDeviceDetails;
using DeviceTelemetry.Application.Mobile.Queries.GetDeviceLifetime;
using DeviceTelemetry.Application.Mobile;
using DeviceTelemetry.Domain.Entities;
using DeviceTelemetry.Infrastructure.Persistence;
using DeviceTelemetry.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DeviceTelemetry.IntegrationTests;

/// <summary><c>payload.lifetime</c> (FW 3.43): latest counters on the device, history on counter changes, mobile API.</summary>
[Collection(PostgresCollection.Name)]
public sealed class DeviceLifetimeTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTime T0 = new(2026, 10, 28, 8, 0, 0, DateTimeKind.Utc);

    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero));
    private string _connectionString = string.Empty;
    private Device _device = null!;

    public async Task InitializeAsync()
    {
        _connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using var db = NewContext();
        _device = await TestData.SeedDeviceAsync(db);
    }

    public async Task DisposeAsync()
    {
        await using var db = NewContext();
        await db.Database.EnsureDeletedAsync();
    }

    private ApplicationDbContext NewContext() => PostgresFixture.CreateContext(_connectionString, _time);

    private static string Payload(long poweredMin, long watchdog, int statusFw = 43, bool stored = true, JsonObject? ack = null) => new JsonObject
    {
        ["fw"] = $"FW 3.{statusFw}",
        ["lifetime"] = new JsonObject
        {
            ["poweredMin"] = poweredMin,
            ["starts"] = 16 + watchdog,
            ["resets"] = new JsonObject
            {
                ["powerOn"] = 9, ["resetPin"] = 2, ["watchdog"] = watchdog, ["supplyDrop"] = 0, ["software"] = 5, ["other"] = 0,
            },
            ["clockLost"] = 3,
            ["fwChanges"] = 4,
            ["firstStartUtc"] = "2026-10-28T12:44:00Z",
            ["stored"] = stored,
            ["ack"] = ack,
        },
        ["values"] = new JsonArray(),
    }.ToJsonString();

    private async Task SendAsync(string? payload, DateTime occurredAtUtc, int statusCode = 100010)
    {
        await using var db = NewContext();
        var result = await TestData.CreateHandler(db, _time).HandleAsync(
            TestData.Command(_device.DeviceCode, statusCode, payload: payload, occurredAtUtc: occurredAtUtc), CancellationToken.None);
        Assert.True(result.IsSuccess);
    }

    private async Task<DeviceDetailsDto> CardAsync()
    {
        await using var db = NewContext();
        return (await new GetDeviceDetailsQueryHandler(db, Options.Create(new MobileFeatureOptions()))
            .HandleAsync(new GetDeviceDetailsQuery(_device.Id), CancellationToken.None)).Value;
    }

    private async Task<List<DeviceLifetimePointDto>> HistoryAsync(int limit = 100)
    {
        await using var db = NewContext();
        return (await new GetDeviceLifetimeHistoryQueryHandler(db)
            .HandleAsync(new GetDeviceLifetimeHistoryQuery(_device.Id, limit), CancellationToken.None)).Value.Items.ToList();
    }

    [Fact]
    public async Task The_card_shows_the_latest_counters_with_their_report_time()
    {
        await SendAsync(Payload(18342, 1), T0, statusCode: 100000);

        var card = await CardAsync();

        Assert.Equal(T0, card.LifetimeAtUtc);
        var lifetime = card.Lifetime!;
        Assert.Equal((18342L, 17L, 3L, 4L, true), (lifetime.PoweredMin, lifetime.Starts, lifetime.ClockLost, lifetime.FwChanges, lifetime.Stored));
        Assert.Equal(1, lifetime.Resets!["watchdog"]);
        Assert.Equal(6, lifetime.Resets.Count);
        Assert.Equal(new DateTime(2026, 10, 28, 12, 44, 0, DateTimeKind.Utc), lifetime.FirstStartUtc);
    }

    [Fact]
    public async Task History_gets_a_point_only_when_a_counter_other_than_powered_minutes_changes()
    {
        await SendAsync(Payload(100, 1), T0);
        await SendAsync(Payload(105, 1), T0.AddMinutes(5));    // only powered time: no point
        await SendAsync(Payload(110, 2), T0.AddMinutes(10));   // watchdog reset: point
        await SendAsync(Payload(115, 2, stored: false), T0.AddMinutes(15)); // flash failure: point

        var history = await HistoryAsync();

        Assert.Equal([T0.AddMinutes(15), T0.AddMinutes(10), T0], history.Select(p => p.AtUtc));
        Assert.Equal([2L, 2L, 1L], history.Select(p => p.Lifetime.Resets!["watchdog"]));
        Assert.Equal(115, (await CardAsync()).Lifetime!.PoweredMin);
    }

    private static JsonObject Ack(long watchdog, string? atUtc) =>
        new() { ["watchdog"] = watchdog, ["supplyDrop"] = 0, ["atUtc"] = atUtc };

    [Fact]
    public async Task The_acknowledgement_is_returned_and_each_new_one_is_a_history_point()
    {
        await SendAsync(Payload(100, 3, ack: Ack(0, null)), T0);                                // never acknowledged
        await SendAsync(Payload(105, 3, ack: Ack(0, null)), T0.AddMinutes(5));                 // nothing new: no point
        await SendAsync(Payload(110, 3, ack: Ack(3, "2026-10-28T08:07:00Z")), T0.AddMinutes(10)); // acknowledged: point
        await SendAsync(Payload(115, 3, ack: Ack(3, "2026-10-28T08:12:00Z")), T0.AddMinutes(15)); // acknowledged again: point

        var ack = (await CardAsync()).Lifetime!.Ack!;
        Assert.Equal(new LifetimeAck(3, 0, new DateTime(2026, 10, 28, 8, 12, 0, DateTimeKind.Utc)), ack);

        var history = await HistoryAsync();
        Assert.Equal([T0.AddMinutes(15), T0.AddMinutes(10), T0], history.Select(p => p.AtUtc));
        Assert.Null(history[^1].Lifetime.Ack!.AtUtc);
    }

    [Fact]
    public async Task Firmware_without_acknowledgements_has_no_ack()
    {
        await SendAsync(Payload(100, 3), T0);

        Assert.Null((await CardAsync()).Lifetime!.Ack);
    }

    [Fact]
    public async Task A_late_report_does_not_overwrite_fresher_counters()
    {
        await SendAsync(Payload(200, 3), T0.AddHours(1));
        await SendAsync(Payload(150, 2), T0); // delivered later, happened earlier

        var card = await CardAsync();
        Assert.Equal((T0.AddHours(1), 200L), (card.LifetimeAtUtc, card.Lifetime!.PoweredMin));
        Assert.Single(await HistoryAsync());
    }

    [Fact]
    public async Task Counters_that_go_down_are_accepted()
    {
        await SendAsync(Payload(5000, 4), T0);
        await SendAsync(Payload(3, 0), T0.AddMinutes(5)); // chip erased or board replaced

        Assert.Equal((3L, 0L), ((await CardAsync()).Lifetime!.PoweredMin, (await CardAsync()).Lifetime!.Resets!["watchdog"]));
        Assert.Equal(2, (await HistoryAsync()).Count);
    }

    [Theory]
    [InlineData("""{"fw":"FW 3.41","values":[]}""")]
    [InlineData("""{"lifetime":"broken"}""")]
    [InlineData("""{"lifetime":null}""")]
    [InlineData(null)]
    public async Task Reports_without_usable_counters_are_stored_and_change_nothing(string? payload)
    {
        await SendAsync(payload, T0);

        var card = await CardAsync();
        Assert.Null(card.Lifetime);
        Assert.Null(card.LifetimeAtUtc);
        Assert.Empty(await HistoryAsync());
        await using var db = NewContext();
        Assert.Equal(1, await db.TelemetryEvents.CountAsync());
    }

    [Fact]
    public async Task Fields_of_an_unexpected_type_are_null_and_the_rest_is_kept()
    {
        await SendAsync("""{"lifetime":{"poweredMin":"lots","starts":3,"resets":{"watchdog":1,"newCause":2,"bad":"x"},"firstStartUtc":null,"stored":1}}""", T0);

        var lifetime = (await CardAsync()).Lifetime!;
        Assert.Null(lifetime.PoweredMin);
        Assert.Equal(3, lifetime.Starts);
        Assert.Equal(new Dictionary<string, long> { ["watchdog"] = 1, ["newCause"] = 2 }, lifetime.Resets);
        Assert.Null(lifetime.FirstStartUtc);
        Assert.Null(lifetime.Stored);
    }

    [Fact]
    public async Task History_pages_with_a_cursor_and_unknown_devices_are_404()
    {
        for (var i = 0; i < 5; i++)
            await SendAsync(Payload(100 + i, i), T0.AddMinutes(i));

        await using var db = NewContext();
        var handler = new GetDeviceLifetimeHistoryQueryHandler(db);
        var first = (await handler.HandleAsync(new GetDeviceLifetimeHistoryQuery(_device.Id, 3), CancellationToken.None)).Value;
        var second = (await handler.HandleAsync(new GetDeviceLifetimeHistoryQuery(_device.Id, 3, first.NextCursor), CancellationToken.None)).Value;

        Assert.Equal([4L, 3L, 2L, 1L, 0L], first.Items.Concat(second.Items).Select(p => p.Lifetime.Resets!["watchdog"]));
        Assert.False(second.HasMore);

        var unknown = await handler.HandleAsync(new GetDeviceLifetimeHistoryQuery(int.MaxValue), CancellationToken.None);
        Assert.Equal("device.not_found", unknown.Error.Code);
    }
}
