using DeviceTelemetry.Application.Outbox;
using DeviceTelemetry.Application.Telemetry;
using DeviceTelemetry.Domain.Entities;
using DeviceTelemetry.Tests.Shared;
using Microsoft.EntityFrameworkCore;

namespace DeviceTelemetry.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class SendTelemetryTests(PostgresFixture postgres)
{
    private static int UniqueStatusCode() => Random.Shared.Next(1_000_000, int.MaxValue);

    [Fact]
    public async Task Event_and_outbox_message_are_stored_together_and_payload_is_jsonb()
    {
        var statusCode = UniqueStatusCode();
        await using var setup = postgres.CreateContext();
        var device = await TestData.SeedDeviceAsync(setup);
        await TestData.SeedTemplateAsync(setup, statusCode);

        await using var db = postgres.CreateContext();
        var result = await TestData.CreateHandler(db, TimeProvider.System).HandleAsync(
            TestData.Command(device.DeviceCode, statusCode, payload: """{"temperature":25.4,"voltage":3.3}"""),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.NotificationQueued);

        await using var verify = postgres.CreateContext();
        var outbox = await verify.OutboxMessages.SingleAsync(m => m.TelemetryEventId == result.Value.EventId);
        Assert.Equal(OutboxMessageStatus.Pending, outbox.Status);

        // Query inside the JSON with PostgreSQL operators: proves the column really is jsonb.
        var temperature = await verify.Database
            .SqlQuery<decimal>($"""SELECT ("Payload"->>'temperature')::numeric AS "Value" FROM "TelemetryEvents" WHERE "Id" = {result.Value.EventId}""")
            .SingleAsync();
        Assert.Equal(25.4m, temperature);

        var columnType = await verify.Database
            .SqlQuery<string>($"""SELECT data_type AS "Value" FROM information_schema.columns WHERE table_name = 'TelemetryEvents' AND column_name = 'Payload'""")
            .SingleAsync();
        Assert.Equal("jsonb", columnType);
    }

    [Fact]
    public async Task Event_is_not_stored_when_its_outbox_message_cannot_be_stored()
    {
        await using var setup = postgres.CreateContext();
        var device = await TestData.SeedDeviceAsync(setup);

        await using var db = postgres.CreateContext();
        var eventId = Guid.CreateVersion7();
        db.TelemetryEvents.Add(new TelemetryEvent
        {
            Id = eventId, DeviceId = device.Id, StatusCode = 1, OccurredAtUtc = DateTime.UtcNow, ReceivedAtUtc = DateTime.UtcNow,
        });
        db.OutboxMessages.Add(new OutboxMessage
        {
            Id = Guid.CreateVersion7(),
            Type = new string('x', OutboxMessage.TypeMaxLength + 1), // violates varchar(100)
            Content = "{}",
            TelemetryEventId = eventId,
            CreatedAtUtc = DateTime.UtcNow,
            NextAttemptAtUtc = DateTime.UtcNow,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        await using var verify = postgres.CreateContext();
        Assert.False(await verify.TelemetryEvents.AnyAsync(e => e.Id == eventId));
    }

    [Fact]
    public async Task Concurrent_retries_with_the_same_message_id_store_one_event_and_one_notification()
    {
        var statusCode = UniqueStatusCode();
        await using var setup = postgres.CreateContext();
        var device = await TestData.SeedDeviceAsync(setup);
        await TestData.SeedTemplateAsync(setup, statusCode);
        var command = TestData.Command(device.DeviceCode, statusCode, messageId: Guid.NewGuid(), payload: """{"n":1}""");

        using var start = new ManualResetEventSlim();
        var requests = Enumerable.Range(0, 10).Select(_ => Task.Run(async () =>
        {
            start.Wait();
            await using var db = postgres.CreateContext();
            return await TestData.CreateHandler(db, TimeProvider.System).HandleAsync(command, CancellationToken.None);
        })).ToList();
        start.Set();
        var results = await Task.WhenAll(requests);

        Assert.All(results, r => Assert.True(r.IsSuccess, r.IsFailure ? r.Error.ToString() : null));
        Assert.Single(results.Select(r => r.Value.EventId).Distinct());
        Assert.Single(results, r => !r.Value.IsDuplicate);

        await using var verify = postgres.CreateContext();
        Assert.Equal(1, await verify.TelemetryEvents.CountAsync(e => e.DeviceId == device.Id));
        Assert.Equal(1, await verify.OutboxMessages.CountAsync(m => m.TelemetryEventId == results[0].Value.EventId));
    }

    [Fact]
    public async Task Reused_message_id_with_different_content_is_rejected()
    {
        await using var setup = postgres.CreateContext();
        var device = await TestData.SeedDeviceAsync(setup);
        var messageId = Guid.NewGuid();

        await using (var db = postgres.CreateContext())
            await TestData.CreateHandler(db, TimeProvider.System).HandleAsync(
                TestData.Command(device.DeviceCode, 1, messageId: messageId), CancellationToken.None);

        await using (var db = postgres.CreateContext())
        {
            var result = await TestData.CreateHandler(db, TimeProvider.System).HandleAsync(
                TestData.Command(device.DeviceCode, 2, messageId: messageId), CancellationToken.None);
            Assert.Equal(TelemetryErrors.MessageIdConflict, result.Error);
        }

        await using var verify = postgres.CreateContext();
        Assert.Equal(1, (await verify.TelemetryEvents.SingleAsync(e => e.DeviceId == device.Id)).StatusCode);
    }
}
