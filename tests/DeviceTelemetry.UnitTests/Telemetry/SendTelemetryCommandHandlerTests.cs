using DeviceTelemetry.Application.Common;
using DeviceTelemetry.Application.Outbox;
using DeviceTelemetry.Application.Telemetry;
using DeviceTelemetry.Application.Telemetry.Commands.SendTelemetry;
using DeviceTelemetry.Infrastructure.Persistence;
using DeviceTelemetry.Tests.Shared;
using Microsoft.EntityFrameworkCore;

namespace DeviceTelemetry.UnitTests.Telemetry;

public sealed class SendTelemetryCommandHandlerTests : IDisposable
{
    private const int StatusCode = 100001;

    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));
    private readonly DbContextOptions<ApplicationDbContext> _options =
        new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
    private readonly ApplicationDbContext _db;

    public SendTelemetryCommandHandlerTests() => _db = NewContext();

    public void Dispose() => _db.Dispose();

    private ApplicationDbContext NewContext() => new(_options, _time);

    /// <summary>Runs the handler with its own context, as a separate HTTP request would.</summary>
    private async Task<Result<SendTelemetryResult>> SendAsync(SendTelemetryCommand command)
    {
        await using var db = NewContext();
        return await TestData.CreateHandler(db, _time).HandleAsync(command, CancellationToken.None);
    }

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
        Assert.Equal("""{"temperature":25.4}""", stored.Payload);
        Assert.Equal(_time.UtcNow, stored.CreatedAt);
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

        Assert.True(result.IsFailure);
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
    }

    [Fact]
    public async Task Event_without_template_is_stored_without_notification()
    {
        var device = await TestData.SeedDeviceAsync(_db);

        var result = await SendAsync(TestData.Command(device.DeviceCode));

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.NotificationQueued);
        await using var db = NewContext();
        Assert.Single(await db.TelemetryEvents.ToListAsync());
        Assert.Empty(await db.OutboxMessages.ToListAsync());
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
        Assert.Equal(OutboxMessageTypes.Email, outbox.Type);
        Assert.Equal(OutboxMessageStatus.Pending, outbox.Status);
        Assert.Equal(0, outbox.Attempts);
        Assert.Equal(result.Value.EventId, outbox.TelemetryEventId);
        Assert.Equal(_time.UtcNow, outbox.NextAttemptAtUtc);

        var email = outbox.ReadEmail();
        Assert.Equal("owner@example.com", email.To);
        Assert.Equal("Device Boiler #1 reported 100001", email.Subject);
        Assert.Equal("Hello ACME Corp, device boiler-17 reported status 100001 at 2026-10-16 10:30:00 UTC.", email.Body);
        Assert.False(email.IsHtml);
    }

    [Fact]
    public async Task Inactive_template_does_not_queue_notification()
    {
        var device = await TestData.SeedDeviceAsync(_db);
        await TestData.SeedTemplateAsync(_db, StatusCode, isActive: false);

        var result = await SendAsync(TestData.Command(device.DeviceCode));

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.NotificationQueued);
        await using var db = NewContext();
        Assert.Empty(await db.OutboxMessages.ToListAsync());
    }

    [Fact]
    public async Task Template_for_other_status_does_not_queue_notification()
    {
        var device = await TestData.SeedDeviceAsync(_db);
        await TestData.SeedTemplateAsync(_db, statusCode: 500);

        var result = await SendAsync(TestData.Command(device.DeviceCode, statusCode: 200));

        Assert.False(result.Value.NotificationQueued);
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
        Assert.Single(await db.TelemetryEvents.ToListAsync());
        Assert.Empty(await db.OutboxMessages.ToListAsync());
    }

    [Fact]
    public async Task Repeated_message_id_returns_original_event_without_new_event_or_notification()
    {
        var device = await TestData.SeedDeviceAsync(_db);
        await TestData.SeedTemplateAsync(_db, StatusCode);
        var messageId = Guid.NewGuid();
        var command = TestData.Command(device.DeviceCode, messageId: messageId, payload: """{"v":1}""");

        var first = await SendAsync(command);
        _time.Advance(TimeSpan.FromSeconds(30));
        var retry = await SendAsync(command);

        Assert.True(retry.IsSuccess);
        Assert.True(retry.Value.IsDuplicate);
        Assert.False(retry.Value.NotificationQueued);
        Assert.Equal(first.Value.EventId, retry.Value.EventId);
        Assert.Equal(first.Value.ReceivedAtUtc, retry.Value.ReceivedAtUtc);

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
        var stored = await db.TelemetryEvents.SingleAsync();
        Assert.Equal("""{"v":1}""", stored.Payload);
    }

    [Fact]
    public async Task Same_message_id_on_different_devices_is_not_a_duplicate()
    {
        var first = await TestData.SeedDeviceAsync(_db);
        var second = await TestData.SeedDeviceAsync(_db);
        var messageId = Guid.NewGuid();

        await SendAsync(TestData.Command(first.DeviceCode, messageId: messageId));
        var result = await SendAsync(TestData.Command(second.DeviceCode, messageId: messageId));

        Assert.False(result.Value.IsDuplicate);
        await using var db = NewContext();
        Assert.Equal(2, await db.TelemetryEvents.CountAsync());
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
        { "empty messageId", TestData.Command("dev", messageId: Guid.Empty) },
        { "occurredAt in the future", TestData.Command("dev", occurredAtUtc: new DateTime(2026, 10, 16, 12, 10, 0, DateTimeKind.Utc)) },
        { "occurredAt before 2000", TestData.Command("dev", occurredAtUtc: new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)) },
        { "occurredAt not UTC", TestData.Command("dev", occurredAtUtc: new DateTime(2026, 10, 16, 11, 0, 0, DateTimeKind.Unspecified)) },
        { "payload not an object", TestData.Command("dev", payload: "[1,2,3]") },
        { "payload not JSON", TestData.Command("dev", payload: "{oops") },
        { "payload too large", TestData.Command("dev", payload: $$"""{"data":"{{new string('x', 9000)}}"}""") },
    };

    [Theory]
    [MemberData(nameof(InvalidCommands))]
    public async Task Invalid_command_returns_validation_error_and_stores_nothing(string _, SendTelemetryCommand command)
    {
        await TestData.SeedDeviceAsync(_db, deviceCode: "dev");

        var result = await SendAsync(command);

        Assert.True(result.IsFailure);
        Assert.Equal(DomainErrorType.Validation, result.Error.Type);
        Assert.Equal("telemetry.invalid", result.Error.Code);
        await using var db = NewContext();
        Assert.Empty(await db.TelemetryEvents.ToListAsync());
    }

    [Fact]
    public async Task Queued_email_keeps_its_content_when_the_template_changes_later()
    {
        var device = await TestData.SeedDeviceAsync(_db);
        var template = await TestData.SeedTemplateAsync(_db, StatusCode, subject: "Original subject");

        await SendAsync(TestData.Command(device.DeviceCode));
        template.Subject = "Changed subject";
        await _db.SaveChangesAsync();

        await using var db = NewContext();
        Assert.Equal("Original subject", (await db.OutboxMessages.SingleAsync()).ReadEmail().Subject);
    }
}
