using System.Data.Common;
using System.Text.Json;
using DeviceTelemetry.Application.Common;
using DeviceTelemetry.Application.Mobile;
using DeviceTelemetry.Application.Mobile.Queries.GetCustomerDevices;
using DeviceTelemetry.Application.Mobile.Queries.GetDeviceDetails;
using DeviceTelemetry.Application.Mobile.Queries.GetDeviceEvents;
using DeviceTelemetry.Application.Mobile.Queries.GetDeviceState;
using DeviceTelemetry.Application.Telemetry;
using DeviceTelemetry.Domain.Entities;
using DeviceTelemetry.Infrastructure.Persistence;
using DeviceTelemetry.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;

namespace DeviceTelemetry.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class MobileQueriesTests(PostgresFixture postgres)
{
    private static readonly DateTime T0 = new(2026, 10, 16, 10, 0, 0, DateTimeKind.Utc);

    private readonly SqlCapture _sql = new();

    /// <summary>A context whose executed SQL is recorded in <see cref="_sql"/>.</summary>
    private ApplicationDbContext CapturingContext() =>
        new(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseNpgsql(postgres.ConnectionString)
                .AddInterceptors(_sql)
                .Options,
            TimeProvider.System);

    private async Task<Customer> SeedCustomerAsync(params string[] deviceNames)
    {
        await using var db = postgres.CreateContext();
        var customer = new Customer { Name = $"Customer {Guid.NewGuid():N}" };
        foreach (var name in deviceNames)
            customer.Devices.Add(new Device { Name = name, DeviceCode = $"dev-{Guid.NewGuid():N}" });
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        return customer;
    }

    private async Task<Device> SeedDeviceAsync() => (await SeedCustomerAsync("Device")).Devices.Single();

    private async Task<List<TelemetryEvent>> SeedEventsAsync(int deviceId, IEnumerable<DateTime> occurredAt, string? payload = null)
    {
        await using var db = postgres.CreateContext();
        var events = occurredAt.Select((t, i) => new TelemetryEvent
        {
            Id = Guid.CreateVersion7(),
            DeviceId = deviceId,
            StatusCode = 1000 + i,
            OccurredAtUtc = t,
            ReceivedAtUtc = t.AddSeconds(2),
            Payload = payload,
        }).ToList();
        db.TelemetryEvents.AddRange(events);
        await db.SaveChangesAsync();
        return events;
    }

    private async Task<Result<PagedResult<DeviceEventDto>>> GetEventsAsync(int deviceId, int limit = 20, string? cursor = null)
    {
        await using var db = postgres.CreateContext();
        return await new GetDeviceEventsQueryHandler(db).HandleAsync(new GetDeviceEventsQuery(deviceId, limit, cursor), CancellationToken.None);
    }

    // ---- GET /customers/{id}/devices ------------------------------------------------------------

    [Fact]
    public async Task Customer_devices_contain_only_that_customers_devices_sorted_by_name_then_id()
    {
        var customer = await SeedCustomerAsync("Boiler", "Alarm", "Boiler");
        var other = await SeedCustomerAsync("Alarm");

        await using var db = CapturingContext();
        var result = await new GetCustomerDevicesQueryHandler(db).HandleAsync(new GetCustomerDevicesQuery(customer.Id), CancellationToken.None);

        var boilers = customer.Devices.Where(d => d.Name == "Boiler").OrderBy(d => d.Id).ToList();
        var alarm = customer.Devices.Single(d => d.Name == "Alarm");
        Assert.Equal(
            [new DeviceListItemDto(alarm.Id, alarm.DeviceCode, "Alarm", null, null),
             new DeviceListItemDto(boilers[0].Id, boilers[0].DeviceCode, "Boiler", null, null),
             new DeviceListItemDto(boilers[1].Id, boilers[1].DeviceCode, "Boiler", null, null)],
            result.Value);
        Assert.DoesNotContain(result.Value, d => d.Id == other.Devices.Single().Id);
        Assert.Single(_sql.Commands); // customer existence and devices in one statement
    }

    [Fact]
    public async Task Customer_without_devices_gets_an_empty_list()
    {
        var customer = await SeedCustomerAsync();

        await using var db = postgres.CreateContext();
        var result = await new GetCustomerDevicesQueryHandler(db).HandleAsync(new GetCustomerDevicesQuery(customer.Id), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value);
    }

    [Fact]
    public async Task Unknown_customer_is_not_found()
    {
        await using var db = postgres.CreateContext();
        var result = await new GetCustomerDevicesQueryHandler(db).HandleAsync(new GetCustomerDevicesQuery(int.MaxValue), CancellationToken.None);

        Assert.Equal(MobileErrors.CustomerNotFound, result.Error);
    }

    // ---- GET /devices/{id}/events --------------------------------------------------------------

    [Fact]
    public async Task Events_contain_only_that_device_newest_first()
    {
        var device = await SeedDeviceAsync();
        var other = await SeedDeviceAsync();
        var events = await SeedEventsAsync(device.Id, [T0, T0.AddMinutes(5), T0.AddMinutes(1)]);
        await SeedEventsAsync(other.Id, [T0.AddMinutes(3)]);

        var page = (await GetEventsAsync(device.Id)).Value;

        Assert.Equal(events.OrderByDescending(e => e.OccurredAtUtc).Select(e => e.Id), page.Items.Select(e => e.Id));
        Assert.False(page.HasMore);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task Device_without_events_gets_an_empty_page()
    {
        var device = await SeedDeviceAsync();

        var result = await GetEventsAsync(device.Id);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value.Items);
        Assert.False(result.Value.HasMore);
        Assert.Null(result.Value.NextCursor);
    }

    [Fact]
    public async Task Unknown_device_events_are_not_found()
    {
        var result = await GetEventsAsync(int.MaxValue);

        Assert.Equal(MobileErrors.DeviceNotFound, result.Error);
    }

    [Fact]
    public async Task Paging_through_events_with_equal_timestamps_returns_every_event_exactly_once_in_order()
    {
        var device = await SeedDeviceAsync();
        // Seven events share one timestamp, so page boundaries fall inside the tie.
        var events = await SeedEventsAsync(device.Id,
            [T0.AddMinutes(2), .. Enumerable.Repeat(T0.AddMinutes(1), 7), T0, T0.AddMinutes(-1)]);

        var collected = new List<DeviceEventDto>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = (await GetEventsAsync(device.Id, limit: 3, cursor)).Value;
            Assert.True(page.Items.Count <= 3);
            collected.AddRange(page.Items);
            cursor = page.NextCursor;
            pages++;
        }
        while (cursor is not null);

        Assert.Equal(4, pages);
        Assert.Equal(events.Count, collected.Select(e => e.Id).Distinct().Count());

        // Expected order is PostgreSQL's own ORDER BY ("OccurredAtUtc" DESC, "Id" DESC) over the same rows.
        await using var db = postgres.CreateContext();
        var expected = await db.TelemetryEvents
            .Where(e => e.DeviceId == device.Id)
            .OrderByDescending(e => e.OccurredAtUtc).ThenByDescending(e => e.Id)
            .Select(e => e.Id)
            .ToListAsync();
        Assert.Equal(expected, collected.Select(e => e.Id));
    }

    [Fact]
    public async Task Page_size_is_capped_at_the_maximum()
    {
        var device = await SeedDeviceAsync();
        await SeedEventsAsync(device.Id, Enumerable.Range(0, GetDeviceEventsQuery.MaxLimit + 5).Select(i => T0.AddSeconds(i)));

        var page = (await GetEventsAsync(device.Id, limit: GetDeviceEventsQuery.MaxLimit)).Value;

        Assert.Equal(GetDeviceEventsQuery.MaxLimit, page.Items.Count);
        Assert.True(page.HasMore);
        Assert.NotNull(page.NextCursor);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(GetDeviceEventsQuery.MaxLimit + 1)]
    public async Task Limit_outside_the_allowed_range_is_rejected(int limit)
    {
        var device = await SeedDeviceAsync();

        var result = await GetEventsAsync(device.Id, limit);

        Assert.Equal("pagination.invalid_limit", result.Error.Code);
        Assert.Equal(DomainErrorType.Validation, result.Error.Type);
    }

    [Theory]
    [InlineData("not-a-cursor!")]
    [InlineData("AAAA")]
    public async Task Malformed_cursor_is_rejected(string cursor)
    {
        var device = await SeedDeviceAsync();

        var result = await GetEventsAsync(device.Id, cursor: cursor);

        Assert.Equal(MobileErrors.InvalidCursor, result.Error);
    }

    [Fact]
    public async Task Payload_is_serialized_as_a_json_object_not_as_a_string()
    {
        var device = await SeedDeviceAsync();
        await SeedEventsAsync(device.Id, [T0], payload: """{"temperature":25.4,"door":"open"}""");
        await SeedEventsAsync(device.Id, [T0.AddMinutes(-1)], payload: null);

        var page = (await GetEventsAsync(device.Id)).Value;
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(page, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        var items = json.RootElement.GetProperty("items");
        var payload = items[0].GetProperty("payload");
        Assert.Equal(JsonValueKind.Object, payload.ValueKind);
        Assert.Equal(25.4m, payload.GetProperty("temperature").GetDecimal());
        Assert.Equal("open", payload.GetProperty("door").GetString());
        Assert.Equal(JsonValueKind.Null, items[1].GetProperty("payload").ValueKind);
    }

    // ---- GET /devices/{id} ---------------------------------------------------------------------

    [Fact]
    public async Task Device_details_report_the_latest_event_by_occurrence_time()
    {
        var device = await SeedDeviceAsync();
        var events = await SeedEventsAsync(device.Id, [T0.AddMinutes(10), T0]);
        // Arrives last but happened earliest (e.g. buffered while offline): it must not become the last known status.
        await SeedEventsAsync(device.Id, [T0.AddMinutes(-30)]);

        await using var db = CapturingContext();
        var result = await new GetDeviceDetailsQueryHandler(db, Options.Create(new MobileFeatureOptions())).HandleAsync(new GetDeviceDetailsQuery(device.Id), CancellationToken.None);

        Assert.Equal(
            new DeviceDetailsDto(device.Id, device.DeviceCode, device.Name, device.CustomerId, T0.AddMinutes(10), events[0].StatusCode, null, null, null, null, true, null, null, null!, false),
            result.Value with { Features = null! });
        Assert.Empty(result.Value.Features);

        // A single statement that reads only the newest event instead of the whole history.
        var sql = Assert.Single(_sql.Commands);
        Assert.Contains("LIMIT 1", sql);
        Assert.DoesNotContain("ROW_NUMBER", sql);
        Assert.DoesNotContain("\"Payload\"", sql);
    }

    [Fact]
    public async Task Device_details_without_events_have_null_last_values()
    {
        var device = await SeedDeviceAsync();

        await using var db = postgres.CreateContext();
        var result = await new GetDeviceDetailsQueryHandler(db, Options.Create(new MobileFeatureOptions())).HandleAsync(new GetDeviceDetailsQuery(device.Id), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value.LastEventAtUtc);
        Assert.Null(result.Value.LastStatusCode);
    }

    [Fact]
    public async Task Unknown_device_details_are_not_found()
    {
        await using var db = postgres.CreateContext();
        var result = await new GetDeviceDetailsQueryHandler(db, Options.Create(new MobileFeatureOptions())).HandleAsync(new GetDeviceDetailsQuery(int.MaxValue), CancellationToken.None);

        Assert.Equal(MobileErrors.DeviceNotFound, result.Error);
    }

    [Fact]
    public async Task History_query_is_served_by_the_device_history_index()
    {
        var device = await SeedDeviceAsync();
        await SeedEventsAsync(device.Id, [T0]);

        await using var db = postgres.CreateContext();
        await db.Database.ExecuteSqlRawAsync("SET enable_seqscan = off"); // tiny test tables would otherwise be scanned
        var plan = await db.Database.SqlQuery<string>(
                $"""
                EXPLAIN SELECT "Id" FROM "TelemetryEvents" WHERE "DeviceId" = {device.Id}
                ORDER BY "OccurredAtUtc" DESC, "Id" DESC LIMIT 21
                """)
            .ToListAsync();

        Assert.Contains(plan, line => line.Contains("IX_TelemetryEvents_DeviceId_OccurredAtUtc_Id"));
        Assert.DoesNotContain(plan, line => line.Contains("Sort"));
    }

    // ---- 1.3: liveness, server events, channels, device state ------------------------------------

    [Fact]
    public async Task Device_list_and_details_report_last_contact_and_silence()
    {
        var device = await SeedDeviceAsync();
        var lastSeen = new DateTime(2026, 10, 16, 9, 0, 0, DateTimeKind.Utc);
        var silentSince = lastSeen.AddMinutes(15);
        await using (var setup = postgres.CreateContext())
        {
            await setup.Devices.Where(d => d.Id == device.Id).ExecuteUpdateAsync(s => s
                .SetProperty(d => d.LastSeenAtUtc, lastSeen)
                .SetProperty(d => d.ReportIntervalS, 300)
                .SetProperty(d => d.SilentSinceUtc, silentSince));
        }

        await using var db = postgres.CreateContext();
        var list = await new GetCustomerDevicesQueryHandler(db).HandleAsync(new GetCustomerDevicesQuery(device.CustomerId), CancellationToken.None);
        var details = await new GetDeviceDetailsQueryHandler(db, Options.Create(new MobileFeatureOptions())).HandleAsync(new GetDeviceDetailsQuery(device.Id), CancellationToken.None);

        var item = Assert.Single(list.Value);
        Assert.Equal((lastSeen, silentSince), (item.LastSeenAtUtc, item.SilentSinceUtc));
        Assert.Equal((lastSeen, 300, silentSince), (details.Value.LastSeenAtUtc, details.Value.ReportIntervalS, details.Value.SilentSinceUtc));
    }

    [Fact]
    public async Task Events_expose_source_channels_and_a_rejected_device_time()
    {
        var device = await SeedDeviceAsync();
        await using (var setup = postgres.CreateContext())
        {
            setup.TelemetryEvents.AddRange(
                new TelemetryEvent
                {
                    Id = Guid.CreateVersion7(), DeviceId = device.Id, StatusCode = 100003, OccurredAtUtc = T0, ReceivedAtUtc = T0,
                    Source = TelemetryEventSource.Device, Channels = """[{"id":"ch2","name":"130AA15P20"}]""",
                    RejectedOccurredAtUtc = T0.AddDays(1),
                },
                new TelemetryEvent
                {
                    Id = Guid.CreateVersion7(), DeviceId = device.Id, StatusCode = 190001, OccurredAtUtc = T0.AddMinutes(-1),
                    ReceivedAtUtc = T0.AddMinutes(-1), Source = TelemetryEventSource.Server,
                },
                new TelemetryEvent // stored before sources were recorded
                {
                    Id = Guid.CreateVersion7(), DeviceId = device.Id, StatusCode = 1, OccurredAtUtc = T0.AddMinutes(-2), ReceivedAtUtc = T0,
                });
            await setup.SaveChangesAsync();
        }

        var items = (await GetEventsAsync(device.Id)).Value.Items;

        Assert.Equal(["device", "server", "device"], items.Select(e => e.Source));
        Assert.Equal([new TelemetryChannel("ch2", "130AA15P20")], items[0].Channels!);
        Assert.Null(items[1].Channels);
        Assert.Equal([true, false, false], items.Select(e => e.OccurredAtRejected));
    }

    [Fact]
    public async Task Device_state_is_the_latest_regular_report_plus_liveness()
    {
        var device = await SeedDeviceAsync();
        await using (var setup = postgres.CreateContext())
        {
            setup.TelemetryEvents.AddRange(
                Report(device.Id, T0, """{"values":{"ch2":{"v":1}}}"""),
                Report(device.Id, T0.AddMinutes(5), """{"values":{"ch2":{"v":2}}}"""),
                Report(device.Id, T0.AddMinutes(-60), """{"values":{"ch2":{"v":0}}}"""), // buffered old report arriving late
                new TelemetryEvent { Id = Guid.CreateVersion7(), DeviceId = device.Id, StatusCode = 100003, OccurredAtUtc = T0.AddMinutes(9), ReceivedAtUtc = T0 });
            await setup.SaveChangesAsync();
        }

        await using var db = CapturingContext();
        var state = (await new GetDeviceStateQueryHandler(db).HandleAsync(new GetDeviceStateQuery(device.Id), CancellationToken.None)).Value;

        Assert.Equal(T0.AddMinutes(5), state.LastReport!.OccurredAtUtc);
        using var payload = JsonDocument.Parse(state.LastReport.Payload!);
        Assert.Equal(2, payload.RootElement.GetProperty("values").GetProperty("ch2").GetProperty("v").GetInt32());
        Assert.Contains(_sql.Commands, c => c.Contains("LIMIT 1") && c.Contains("100010"));
    }

    [Fact]
    public async Task Device_state_without_reports_has_no_last_report_and_unknown_device_is_not_found()
    {
        var device = await SeedDeviceAsync();

        await using var db = postgres.CreateContext();
        var handler = new GetDeviceStateQueryHandler(db);
        var state = await handler.HandleAsync(new GetDeviceStateQuery(device.Id), CancellationToken.None);
        var unknown = await handler.HandleAsync(new GetDeviceStateQuery(int.MaxValue), CancellationToken.None);

        Assert.Null(state.Value.LastReport);
        Assert.Equal(MobileErrors.DeviceNotFound, unknown.Error);
    }

    [Fact]
    public async Task Last_report_lookup_is_served_by_the_device_status_index()
    {
        var device = await SeedDeviceAsync();
        await using var db = postgres.CreateContext();
        await db.Database.ExecuteSqlRawAsync("SET enable_seqscan = off");
        var plan = await db.Database.SqlQuery<string>(
                $"""
                EXPLAIN SELECT "Id" FROM "TelemetryEvents" WHERE "DeviceId" = {device.Id} AND "StatusCode" = 100010
                ORDER BY "OccurredAtUtc" DESC, "Id" DESC LIMIT 1
                """)
            .ToListAsync();

        Assert.Contains(plan, line => line.Contains("IX_TelemetryEvents_DeviceId_StatusCode_OccurredAtUtc_Id"));
        Assert.DoesNotContain(plan, line => line.Contains("Sort"));
    }

    private static TelemetryEvent Report(int deviceId, DateTime occurredAt, string payload) => new()
    {
        Id = Guid.CreateVersion7(), DeviceId = deviceId, StatusCode = 100010, OccurredAtUtc = occurredAt, ReceivedAtUtc = T0, Payload = payload,
    };

    private sealed class SqlCapture : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
