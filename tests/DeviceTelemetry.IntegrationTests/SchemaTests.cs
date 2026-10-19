using DeviceTelemetry.Application.Common.Exceptions;
using DeviceTelemetry.Domain.Entities;
using DeviceTelemetry.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace DeviceTelemetry.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class SchemaTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Migrations_are_applied_and_match_the_model()
    {
        await using var db = postgres.CreateContext();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges(), "The model has changes not covered by a migration.");
    }

    [Fact]
    public async Task Upgrade_from_1_2_0_backfills_last_contact_from_existing_events()
    {
        var connectionString = new Npgsql.NpgsqlConnectionStringBuilder(postgres.ConnectionString)
        {
            Database = $"device_telemetry_upgrade_{Guid.NewGuid():N}",
        }.ConnectionString;
        await using var db = PostgresFixture.CreateContext(connectionString);
        var migrator = db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
        try
        {
            await migrator.MigrateAsync("20261016153232_DeviceHistoryIndexTieBreaker"); // schema of 1.2.0
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO "Customers" ("Name", "CreatedAt") VALUES ('C', now());
                INSERT INTO "Devices" ("Name", "DeviceCode", "CustomerId", "CreatedAt")
                    SELECT 'with events', 'upg-1', "Id", now() FROM "Customers"
                    UNION ALL SELECT 'without events', 'upg-2', "Id", now() FROM "Customers";
                INSERT INTO "TelemetryEvents" ("Id", "DeviceId", "StatusCode", "OccurredAtUtc", "ReceivedAtUtc", "CreatedAt")
                    SELECT gen_random_uuid(), "Id", 1, t, t, now()
                    FROM "Devices", (VALUES (timestamptz '2026-10-16 08:00Z'), (timestamptz '2026-10-16 09:30Z')) AS v(t)
                    WHERE "DeviceCode" = 'upg-1';
                """);

            await migrator.MigrateAsync(); // to the current version

            var lastSeen = await db.Devices.OrderBy(d => d.DeviceCode).Select(d => d.LastSeenAtUtc).ToListAsync();
            Assert.Equal([new DateTime(2026, 10, 16, 9, 30, 0, DateTimeKind.Utc), null], lastSeen);
            Assert.True(await db.TelemetryEvents.AllAsync(e => e.Source == null && e.Channels == null));
        }
        finally
        {
            await db.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task Device_code_is_unique()
    {
        await using var db = postgres.CreateContext();
        var device = await TestData.SeedDeviceAsync(db);

        await using var other = postgres.CreateContext();
        var duplicate = new Device { Name = "Copy", DeviceCode = device.DeviceCode, CustomerId = device.CustomerId };
        other.Devices.Add(duplicate);

        var ex = await Assert.ThrowsAsync<UniqueConstraintViolationException>(() => other.SaveChangesAsync());
        Assert.Equal("IX_Devices_DeviceCode", ex.ConstraintName);
    }

    [Fact]
    public async Task Device_must_reference_an_existing_customer()
    {
        await using var db = postgres.CreateContext();
        db.Devices.Add(new Device { Name = "Orphan", DeviceCode = $"orphan-{Guid.NewGuid():N}", CustomerId = int.MaxValue });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Only_one_template_per_status_code()
    {
        var statusCode = Random.Shared.Next(1_000_000, int.MaxValue);
        await using var db = postgres.CreateContext();
        await TestData.SeedTemplateAsync(db, statusCode);

        await using var other = postgres.CreateContext();
        var ex = await Assert.ThrowsAsync<UniqueConstraintViolationException>(() => TestData.SeedTemplateAsync(other, statusCode));
        Assert.Equal("IX_Templates_StatusCode_Language", ex.ConstraintName);
    }

    [Fact]
    public async Task A_status_code_has_one_template_per_language()
    {
        var statusCode = Random.Shared.Next(1_000_000, int.MaxValue);
        await using var db = postgres.CreateContext();
        await TestData.SeedTemplateAsync(db, statusCode, language: "en");
        await TestData.SeedTemplateAsync(db, statusCode, language: "ru");

        await using var other = postgres.CreateContext();
        var ex = await Assert.ThrowsAsync<UniqueConstraintViolationException>(() => TestData.SeedTemplateAsync(other, statusCode, language: "ru"));
        Assert.Equal("IX_Templates_StatusCode_Language", ex.ConstraintName);
    }
}
