using DeviceTelemetry.Application.Devices;
using DeviceTelemetry.Infrastructure.Persistence.Seeding;
using DeviceTelemetry.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DeviceTelemetry.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class StagingDataSeederTests(PostgresFixture postgres) : IAsyncLifetime
{
    private string _connectionString = string.Empty;

    public async Task InitializeAsync() => _connectionString = await postgres.CreateEmptyDatabaseAsync();

    public async Task DisposeAsync()
    {
        await using var db = PostgresFixture.CreateContext(_connectionString);
        await db.Database.EnsureDeletedAsync();
    }

    private async Task<IReadOnlyList<SeededDevice>> SeedAsync(string? recipientEmail = null)
    {
        await using var db = PostgresFixture.CreateContext(_connectionString);
        var options = Options.Create(new StagingSeedOptions { RecipientEmail = recipientEmail });
        return await new StagingDataSeeder(db, options, NullLogger<StagingDataSeeder>.Instance).SeedAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Customers_get_plus_addresses_of_the_configured_recipient()
    {
        await SeedAsync(recipientEmail: "owner@gmail.com");

        await using var db = PostgresFixture.CreateContext(_connectionString);
        var emails = await db.Customers.OrderBy(c => c.Id).Select(c => c.Email).ToListAsync();
        Assert.Equal("owner+customer01@gmail.com", emails[0]);
        Assert.Equal("owner+customer10@gmail.com", emails[^1]);
    }

    [Fact]
    public async Task Without_recipient_customers_get_example_addresses()
    {
        await SeedAsync();

        await using var db = PostgresFixture.CreateContext(_connectionString);
        Assert.True(await db.Customers.AllAsync(c => c.Email!.EndsWith("@example.com")));
    }

    [Fact]
    public async Task Seeds_customers_devices_and_templates_with_working_api_keys()
    {
        var seeded = await SeedAsync();

        Assert.Equal(StagingDataSeeder.DeviceCount, seeded.Count);
        await using var db = PostgresFixture.CreateContext(_connectionString);
        Assert.Equal(StagingDataSeeder.CustomerCount, await db.Customers.CountAsync());
        Assert.Equal(StagingDataSeeder.DeviceCount, await db.Devices.CountAsync());
        Assert.True(await db.Customers.AllAsync(c => c.Devices.Count == StagingDataSeeder.DeviceCount / StagingDataSeeder.CustomerCount));
        Assert.Equal(2, await db.Templates.CountAsync(t => t.IsActive));

        var hashes = await db.Devices.ToDictionaryAsync(d => d.DeviceCode, d => d.ApiKeyHash);
        Assert.All(seeded, d => Assert.True(DeviceApiKey.Verify(d.ApiKey, hashes[d.DeviceCode])));
        Assert.Equal(seeded.Count, seeded.Select(d => d.ApiKey).Distinct().Count());
    }

    [Fact]
    public async Task Seeded_device_can_send_telemetry_that_triggers_a_notification()
    {
        var device = (await SeedAsync())[0];

        await using var db = PostgresFixture.CreateContext(_connectionString);
        var result = await TestData.CreateHandler(db, TimeProvider.System).HandleAsync(
            TestData.Command(device.DeviceCode, statusCode: 100001, deviceKey: device.ApiKey), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.NotificationQueued);
    }

    [Fact]
    public async Task Second_run_leaves_existing_data_and_keys_untouched()
    {
        var first = await SeedAsync();

        var second = await SeedAsync();

        Assert.Empty(second);
        await using var db = PostgresFixture.CreateContext(_connectionString);
        Assert.Equal(StagingDataSeeder.DeviceCount, await db.Devices.CountAsync());
        var device = await db.Devices.SingleAsync(d => d.DeviceCode == first[0].DeviceCode);
        Assert.True(DeviceApiKey.Verify(first[0].ApiKey, device.ApiKeyHash));
    }

    [Fact]
    public async Task Concurrent_instances_seed_only_once()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(() => SeedAsync())));

        Assert.Single(results, r => r.Count == StagingDataSeeder.DeviceCount);
        await using var db = PostgresFixture.CreateContext(_connectionString);
        Assert.Equal(StagingDataSeeder.CustomerCount, await db.Customers.CountAsync());
    }
}
