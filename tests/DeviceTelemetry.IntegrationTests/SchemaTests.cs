using DeviceTelemetry.Application.Common.Exceptions;
using DeviceTelemetry.Domain.Entities;
using DeviceTelemetry.Tests.Shared;
using Microsoft.EntityFrameworkCore;

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
        Assert.Equal("IX_Templates_StatusCode", ex.ConstraintName);
    }
}
