using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeviceTelemetry.Application.Common;
using DeviceTelemetry.Application.Outbox;
using DeviceTelemetry.Application.Telemetry;
using DeviceTelemetry.Application.Telemetry.Commands.SendTelemetry;
using DeviceTelemetry.Domain.Entities;
using DeviceTelemetry.Infrastructure.Persistence;
using DeviceTelemetry.Infrastructure.Persistence.Seeding;
using DeviceTelemetry.Tests.Shared;
using Microsoft.EntityFrameworkCore;

namespace DeviceTelemetry.IntegrationTests;

/// <summary>Telemetry ingestion against PostgreSQL; every test gets its own empty, migrated database.</summary>
[Collection(PostgresCollection.Name)]
public sealed class SendTelemetryCommandHandlerTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const int StatusCode = 100001;

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

    /// <summary>Runs the handler with its own context, as a separate HTTP request would.</summary>
    private async Task<Result<SendTelemetryResult>> SendAsync(SendTelemetryCommand command)
    {
        await using var db = NewContext();
        return await TestData.CreateHandler(db, _time).HandleAsync(command, CancellationToken.None);
    }

    private async Task<Device> ReloadDeviceAsync(int id)
    {
        await using var db = NewContext();
        return await db.Devices.AsNoTracking().SingleAsync(d => d.Id == id);
    }

    private async Task<List<string>> RecipientsAsync()
    {
        await using var db = NewContext();
        return (await db.OutboxMessages.OrderBy(m => m.CreatedAtUtc).ThenBy(m => m.Id).ToListAsync())
            .Select(m => m.ReadEmail().To).ToList();
    }

    private async Task SeedDefaultTemplatesAsync()
    {
        _db.Templates.AddRange(DefaultTemplates.All.Select(DefaultTemplates.Copy));
        await _db.SaveChangesAsync();
    }

    private static string Json(string? json) => JsonSerializer.Serialize(JsonDocument.Parse(json!).RootElement);

    // ---- Existing behaviour (unchanged contract) --------------------------------------------------

    [Fact]
    public async Task Valid_message_from_registered_device_is_stored()
    {
        var device = await TestData.SeedDeviceAsync(_db);
        var occurredAt = _time.UtcNow.AddMinutes(-3);

        var result = await SendAsync(TestData.Command(device.DeviceCode, payload: """{"temperature":25.4}""", occurredAtUtc: occurredAt));

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.IsDuplicate);
        Assert.Equal(_time.UtcNow, result.Value.ReceivedAtUtc);

        await using var db = NewContext();
        var stored = await db.TelemetryEvents.SingleAsync();
        Assert.Equal(result.Value.EventId, stored.Id);
        Assert.Equal(device.Id, stored.DeviceId);
        Assert.Equal(StatusCode, stored.StatusCode);
        Assert.Equal(occurredAt, stored.OccurredAtUtc);
        Assert.Equal(_time.UtcNow, stored.ReceivedAtUtc);
        Assert.Equal("""{"temperature":25.4}""", Json(stored.Payload));
        Assert.Equal(TelemetryEventSource.Device, stored.Source);
        Assert.Null(stored.Channels);
        Assert.Equal(_time.UtcNow, (await ReloadDeviceAsync(device.Id)).LastSeenAtUtc);
    }

    [Fact]
    public async Task Missing_occurred_at_defaults_to_receive_time()
    {
        var device = await TestData.SeedDeviceAsync(_db);

        await SendAsync(TestData.Command(device.DeviceCode));

        await using var db = NewContext();
        var stored = await db.TelemetryEvents.SingleAsync();
        Assert.Equal(_time.UtcNow, stored.OccurredAtUtc);
        Assert.Null(stored.Payload);
    }

    [Fact]
    public async Task Unknown_device_is_rejected_and_nothing_is_stored()
    {
        var result = await SendAsync(TestData.Command("no-such-device"));

        Assert.Equal(TelemetryErrors.DeviceAuthenticationFailed, result.Error);
        await using var db = NewContext();
        Assert.Empty(await db.TelemetryEvents.ToListAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("dtk_wrong-key")]
    public async Task Missing_or_wrong_device_key_is_rejected(string? deviceKey)
    {
        var device = await TestData.SeedDeviceAsync(_db);

        var result = await SendAsync(TestData.Command(device.DeviceCode, deviceKey: deviceKey));

        Assert.Equal(DomainErrorType.Unauthorized, result.Error.Type);
        await using var db = NewContext();
        Assert.Empty(await db.TelemetryEvents.ToListAsync());
        Assert.Null((await ReloadDeviceAsync(device.Id)).LastSeenAtUtc);
    }

    [Fact]
    public async Task Active_template_queues_rendered_email_in_outbox()
    {
        var device = await TestData.SeedDeviceAsync(_db, deviceCode: "boiler-17", customerName: "ACME Corp", deviceName: "Boiler #1");
        await TestData.SeedTemplateAsync(_db, StatusCode);

        var result = await SendAsync(TestData.Command(device.DeviceCode, occurredAtUtc: new DateTime(2026, 10, 16, 10, 30, 0, DateTimeKind.Utc)));

        Assert.True(result.Value.NotificationQueued);
        await using var db = NewContext();
        var outbox = await db.OutboxMessages.SingleAsync();
        Assert.Equal(OutboxMessageStatus.Pending, outbox.Status);
        Assert.Equal(result.Value.EventId, outbox.TelemetryEventId);
        var email = outbox.ReadEmail();
        Assert.Equal("owner@example.com", email.To);
        Assert.Equal("Device Boiler #1 reported 100001", email.Subject);
        Assert.Equal("Hello ACME Corp, device boiler-17 reported status 100001 at 2026-10-16 10:30:00 UTC.", email.Body);
    }

    [Fact]
    public async Task Event_without_template_or_with_inactive_template_is_stored_without_notification()
    {
        var device = await TestData.SeedDeviceAsync(_db);
        await TestData.SeedTemplateAsync(_db, 500, isActive: false);

        var noTemplate = await SendAsync(TestData.Command(device.DeviceCode, statusCode: 200));
        var inactive = await SendAsync(TestData.Command(device.DeviceCode, statusCode: 500));

        Assert.False(noTemplate.Value.NotificationQueued);
        Assert.False(inactive.Value.NotificationQueued);
        await using var db = NewContext();
        Assert.Equal(2, await db.TelemetryEvents.CountAsync());
        Assert.Empty(await db.OutboxMessages.ToListAsync());
    }

    [Fact]
    public async Task Template_error_keeps_the_event_and_skips_notification()
    {
        var device = await TestData.SeedDeviceAsync(_db);
        await TestData.SeedTemplateAsync(_db, StatusCode, body: "Battery at {BatteryLevel}%");

        var result = await SendAsync(TestData.Command(device.DeviceCode));

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.NotificationQueued);
        await using var db = NewContext();
        Assert.Single(await db.TelemetryEvents.ToListAsync());
        Assert.Empty(await db.OutboxMessages.ToListAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-an-email")]
    public async Task Customer_without_valid_email_keeps_the_event_and_skips_notification(string? email)
    {
        var device = await TestData.SeedDeviceAsync(_db, customerEmail: email);
        await TestData.SeedTemplateAsync(_db, StatusCode);

        var result = await SendAsync(TestData.Command(device.DeviceCode));

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.NotificationQueued);
        await using var db = NewContext();
        Assert.Empty(await db.OutboxMessages.ToListAsync());
    }

    [Fact]
    public async Task Repeated_message_id_returns_original_event_without_new_event_or_notification()
    {
        var device = await TestData.SeedDeviceAsync(_db);
        await TestData.SeedTemplateAsync(_db, StatusCode);
        var command = TestData.Command(device.DeviceCode, messageId: Guid.NewGuid(), payload: """{"v":1}""");

        var first = await SendAsync(command);
        _time.Advance(TimeSpan.FromSeconds(30));
        var retry = await SendAsync(command);

        Assert.True(retry.Value.IsDuplicate);
        Assert.False(retry.Value.NotificationQueued);
        Assert.Equal(first.Value.EventId, retry.Value.EventId);
        await using var db = NewContext();
        Assert.Single(await db.TelemetryEvents.ToListAsync());
        Assert.Single(await db.OutboxMessages.ToListAsync());
    }

    [Fact]
    public async Task Repeated_message_id_with_different_content_is_a_conflict_and_keeps_the_original()
    {
        var device = await TestData.SeedDeviceAsync(_db);
        var messageId = Guid.NewGuid();

        await SendAsync(TestData.Command(device.DeviceCode, messageId: messageId, payload: """{"v":1}"""));
        var conflicting = await SendAsync(TestData.Command(device.DeviceCode, messageId: messageId, payload: """{"v":2}"""));

        Assert.Equal(TelemetryErrors.MessageIdConflict, conflicting.Error);
        await using var db = NewContext();
        Assert.Equal("""{"v":1}""", Json((await db.TelemetryEvents.SingleAsync()).Payload));
    }

    [Fact]
    public async Task Identical_messages_without_message_id_are_stored_as_separate_events()
    {
        var device = await TestData.SeedDeviceAsync(_db);
        var command = TestData.Command(device.DeviceCode, payload: """{"door":"open"}""");

        await SendAsync(command);
        var second = await SendAsync(command);

        Assert.False(second.Value.IsDuplicate);
        await using var db = NewContext();
        Assert.Equal(2, await db.TelemetryEvents.CountAsync());
    }

    public static TheoryData<string, SendTelemetryCommand> InvalidCommands => new()
    {
        { "empty deviceId", TestData.Command("") },
        { "too long deviceId", TestData.Command(new string('x', 101)) },
        { "negative statusCode", TestData.Command("dev", statusCode: -1) },
        { "server status 190001", TestData.Command("dev", statusCode: 190001) },
        { "server status 199999", TestData.Command("dev", statusCode: 199999) },
        { "empty messageId", TestData.Command("dev", messageId: Guid.Empty) },
        { "occurredAt before 2000", TestData.Command("dev", occurredAtUtc: new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)) },
        { "occurredAt not UTC", TestData.Command("dev", occurredAtUtc: new DateTime(2026, 10, 16, 11, 0, 0, DateTimeKind.Unspecified)) },
        { "payload not an object", TestData.Command("dev", payload: "[1,2,3]") },
        { "payload too large", TestData.Command("dev", payload: $$"""{"data":"{{new string('x', 9000)}}"}""") },
        { "33 channels", TestData.Command("dev", channels: Enumerable.Range(0, 33).Select(i => new TelemetryChannel($"ch{i}", null)).ToList()) },
        { "channel without id", TestData.Command("dev", channels: [new TelemetryChannel("", "x")]) },
        { "channel id too long", TestData.Command("dev", channels: [new TelemetryChannel(new string('c', 17), null)]) },
        { "channel name too long", TestData.Command("dev", channels: [new TelemetryChannel("ch1", new string('n', 33))]) },
        { "null channel", TestData.Command("dev", channels: [null!]) },
    };

    [Theory]
    [MemberData(nameof(InvalidCommands))]
    public async Task Invalid_command_returns_validation_error_and_stores_nothing(string _, SendTelemetryCommand command)
    {
        await TestData.SeedDeviceAsync(_db, deviceCode: "dev");

        var result = await SendAsync(command);

        Assert.Equal("telemetry.invalid", result.Error.Code);
        await using var db = NewContext();
        Assert.Empty(await db.TelemetryEvents.ToListAsync());
    }

    // ---- Hash compatibility -------------------------------------------------------------------

    /// <summary>The formula used up to 1.2.0, reproduced independently of the handler.</summary>
    private static byte[] HashBeforeVersion13(int statusCode, DateTime? occurredAtUtc, string? payload) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(
            '\n',
            statusCode.ToString(CultureInfo.InvariantCulture),
            occurredAtUtc?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
            payload ?? string.Empty)));

    [Fact]
    public async Task Retry_of_an_event_accepted_before_the_update_is_still_a_duplicate()
    {
        var device = await TestData.SeedDeviceAsync(_db);
        var messageId = Guid.NewGuid();
        var occurredAt = new DateTime(2026, 10, 16, 11, 0, 0, DateTimeKind.Utc);
        const string payload = """{"v":1}""";
        var original = new TelemetryEvent
        {
            Id = Guid.CreateVersion7(),
            DeviceId = device.Id,
            StatusCode = 100003,
            OccurredAtUtc = occurredAt,
            ReceivedAtUtc = occurredAt,
            Payload = payload,
            MessageId = messageId,
            ContentHash = HashBeforeVersion13(100003, occurredAt, payload),
        };
        _db.TelemetryEvents.Add(original);
        await _db.SaveChangesAsync();

        // Same body an old firmware sends again; new optional fields that are not hashed may differ.
        var retry = await SendAsync(TestData.Command(
            device.DeviceCode, 100003, messageId, payload, occurredAt, notifyEmail: "new@example.com", reportIntervalS: 300));

        Assert.True(retry.Value.IsDuplicate);
        Assert.Equal(original.Id, retry.Value.EventId);
    }

    [Fact]
    public async Task Channels_are_hashed_only_when_sent_and_their_order_matters()
    {
        var device = await TestData.SeedDeviceAsync(_db);
        TelemetryChannel[] channels = [new("ch2", "130AA15P20"), new("ch5", "130AA15P21")];
        var messageId = Guid.NewGuid();

        var first = await SendAsync(TestData.Command(device.DeviceCode, 100003, messageId, channels: channels));
        var sameOrder = await SendAsync(TestData.Command(device.DeviceCode, 100003, messageId, channels: [.. channels]));
        var otherOrder = await SendAsync(TestData.Command(device.DeviceCode, 100003, messageId, channels: [channels[1], channels[0]]));
        var otherSet = await SendAsync(TestData.Command(device.DeviceCode, 100003, messageId, channels: [channels[0]]));
        var without = await SendAsync(TestData.Command(device.DeviceCode, 100003, messageId));

        Assert.True(sameOrder.Value.IsDuplicate);
        Assert.Equal(first.Value.EventId, sameOrder.Value.EventId);
        Assert.Equal(TelemetryErrors.MessageIdConflict, otherOrder.Error);
        Assert.Equal(TelemetryErrors.MessageIdConflict, otherSet.Error);
        Assert.Equal(TelemetryErrors.MessageIdConflict, without.Error);

        await using var db = NewContext();
        Assert.Equal("""[{"id":"ch2","name":"130AA15P20"},{"id":"ch5","name":"130AA15P21"}]""",
            Json((await db.TelemetryEvents.SingleAsync()).Channels));
    }

    // ---- notifyEmail ---------------------------------------------------------------------------

    [Fact]
    public async Task Notify_email_is_stored_and_used_instead_of_the_customer_email()
    {
        var device = await TestData.SeedDeviceAsync(_db);
        await TestData.SeedTemplateAsync(_db, StatusCode);

        await SendAsync(TestData.Command(device.DeviceCode, notifyEmail: " site-7@example.org "));
        await SendAsync(TestData.Command(device.DeviceCode)); // no field: address kept

        Assert.Equal("site-7@example.org", (await ReloadDeviceAsync(device.Id)).NotifyEmail);
        Assert.Equal(["site-7@example.org", "site-7@example.org"], await RecipientsAsync());
    }

    [Fact]
    public async Task Empty_notify_email_clears_the_stored_address()
    {
        var device = await TestData.SeedDeviceAsync(_db);
        await TestData.SeedTemplateAsync(_db, StatusCode);
        await SendAsync(TestData.Command(device.DeviceCode, statusCode: 200, notifyEmail: "site-7@example.org"));

        await SendAsync(TestData.Command(device.DeviceCode, notifyEmail: ""));

        Assert.Null((await ReloadDeviceAsync(device.Id)).NotifyEmail);
        Assert.Equal(["owner@example.com"], await RecipientsAsync());
    }

    [Theory]
    [InlineData("не адрес")]
    [InlineData("Site Seven <site-7@example.org>")]
    public async Task Invalid_notify_email_is_ignored_and_recorded_but_the_event_and_email_go_through(string invalid)
    {
        var device = await TestData.SeedDeviceAsync(_db);
        await TestData.SeedTemplateAsync(_db, StatusCode);

        var result = await SendAsync(TestData.Command(device.DeviceCode, notifyEmail: invalid));

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.NotificationQueued);
        Assert.Null((await ReloadDeviceAsync(device.Id)).NotifyEmail);
        Assert.Equal(["owner@example.com"], await RecipientsAsync());
        await using var db = NewContext();
        Assert.Equal(invalid, (await db.TelemetryEvents.SingleAsync()).RejectedNotifyEmail);
    }

    [Fact]
    public async Task Invalid_notify_email_keeps_a_previously_valid_address()
    {
        var device = await TestData.SeedDeviceAsync(_db);
        await SendAsync(TestData.Command(device.DeviceCode, statusCode: 200, notifyEmail: "site-7@example.org"));

        await SendAsync(TestData.Command(device.DeviceCode, statusCode: 200, notifyEmail: "typo@"));

        Assert.Equal("site-7@example.org", (await ReloadDeviceAsync(device.Id)).NotifyEmail);
    }

    // ---- reportIntervalS -----------------------------------------------------------------------

    [Fact]
    public async Task Report_interval_is_stored_and_kept_until_changed()
    {
        var device = await TestData.SeedDeviceAsync(_db);

        await SendAsync(TestData.Command(device.DeviceCode, statusCode: 100010, reportIntervalS: 300));
        await SendAsync(TestData.Command(device.DeviceCode, statusCode: 100010));

        Assert.Equal(300, (await ReloadDeviceAsync(device.Id)).ReportIntervalS);
    }

    [Theory]
    [InlineData(9)]
    [InlineData(86_401)]
    [InlineData(-5)]
    public async Task Out_of_range_report_interval_is_ignored_and_recorded_but_the_event_is_accepted(int interval)
    {
        var device = await TestData.SeedDeviceAsync(_db);
        await SendAsync(TestData.Command(device.DeviceCode, statusCode: 100010, reportIntervalS: 60));

        var result = await SendAsync(TestData.Command(device.DeviceCode, statusCode: 100010, reportIntervalS: interval));

        Assert.True(result.IsSuccess);
        Assert.Equal(60, (await ReloadDeviceAsync(device.Id)).ReportIntervalS);
        await using var db = NewContext();
        Assert.Equal(interval, (await db.TelemetryEvents.SingleAsync(e => e.Id == result.Value.EventId)).RejectedReportIntervalS);
    }

    // ---- occurredAtUtc in the future -----------------------------------------------------------

    [Fact]
    public async Task Future_occurred_at_is_accepted_with_the_receive_time_and_retries_still_deduplicate()
    {
        var device = await TestData.SeedDeviceAsync(_db);
        var future = _time.UtcNow.AddHours(3);
        var command = TestData.Command(device.DeviceCode, messageId: Guid.NewGuid(), occurredAtUtc: future);

        var result = await SendAsync(command);
        _time.Advance(TimeSpan.FromMinutes(1));
        var retry = await SendAsync(command);

        Assert.True(result.IsSuccess);
        Assert.True(retry.Value.IsDuplicate);
        await using var db = NewContext();
        var stored = await db.TelemetryEvents.SingleAsync();
        Assert.Equal(stored.ReceivedAtUtc, stored.OccurredAtUtc);
        Assert.Equal(future, stored.RejectedOccurredAtUtc);
    }

    [Fact]
    public async Task Occurred_at_within_the_clock_skew_is_kept()
    {
        var device = await TestData.SeedDeviceAsync(_db);
        var slightlyAhead = _time.UtcNow.AddMinutes(4);

        await SendAsync(TestData.Command(device.DeviceCode, occurredAtUtc: slightlyAhead));

        await using var db = NewContext();
        var stored = await db.TelemetryEvents.SingleAsync();
        Assert.Equal(slightlyAhead, stored.OccurredAtUtc);
        Assert.Null(stored.RejectedOccurredAtUtc);
    }

    // ---- Reference templates -------------------------------------------------------------------

    [Fact]
    public async Task Channels_placeholder_renders_the_list_and_renders_empty_without_it()
    {
        var device = await TestData.SeedDeviceAsync(_db, deviceName: "Boiler room");
        await SeedDefaultTemplatesAsync();

        var withChannels = await SendAsync(TestData.Command(
            device.DeviceCode, 100003, channels: [new("ch2", "130AA15P20"), new("ch5", "130AA15P21")]));
        var withoutChannels = await SendAsync(TestData.Command(device.DeviceCode, 100003));

        Assert.True(withChannels.Value.NotificationQueued);
        Assert.True(withoutChannels.Value.NotificationQueued);
        await using var db = NewContext();
        var emails = (await db.OutboxMessages.ToListAsync()).Select(m => (m.TelemetryEventId, Email: m.ReadEmail())).ToList();
        var listed = emails.Single(e => e.TelemetryEventId == withChannels.Value.EventId).Email;
        var unlisted = emails.Single(e => e.TelemetryEventId == withoutChannels.Value.EventId).Email;
        Assert.Equal("Boiler room: channels not responding ch2 (130AA15P20), ch5 (130AA15P21)", listed.Subject);
        Assert.Contains("Channels: ch2 (130AA15P20), ch5 (130AA15P21)\n", listed.Body);
        Assert.Equal("Boiler room: channels not responding", unlisted.Subject);
        Assert.Contains("Channels: \n", unlisted.Body);
    }

    [Fact]
    public async Task Regular_report_100010_creates_no_email()
    {
        var device = await TestData.SeedDeviceAsync(_db);
        await SeedDefaultTemplatesAsync();

        var result = await SendAsync(TestData.Command(device.DeviceCode, TelemetryStatusCodes.RegularReport, payload: """{"values":{}}"""));

        Assert.False(result.Value.NotificationQueued);
        await using var db = NewContext();
        Assert.Empty(await db.OutboxMessages.ToListAsync());
    }
}
