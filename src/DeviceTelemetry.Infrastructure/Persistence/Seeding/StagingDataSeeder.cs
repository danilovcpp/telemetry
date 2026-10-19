using System.Globalization;
using DeviceTelemetry.Application.Devices;
using DeviceTelemetry.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DeviceTelemetry.Infrastructure.Persistence.Seeding;

/// <summary>
/// Fills an empty Staging database with test customers and devices (with API keys), and on every start adds the
/// reference notification templates (<see cref="DefaultTemplates"/>) whose status codes are missing.
/// Customers and devices are created once: if any customer exists they are left untouched, so restarts never duplicate
/// data or rotate keys. Existing templates are never overwritten (use <c>sync-templates</c> for that).
/// The plain API keys are logged because nothing else can recover them later; never call this outside Staging.
/// </summary>
public sealed class StagingDataSeeder(
    ApplicationDbContext db,
    IOptions<StagingSeedOptions> options,
    ILogger<StagingDataSeeder> logger)
{
    public const int CustomerCount = 10;
    public const int DeviceCount = 50;

    /// <summary>Arbitrary key for <c>pg_advisory_xact_lock</c>, so concurrently starting instances seed only once.</summary>
    private const long AdvisoryLockKey = 0x4454_5345_4544;

    public async Task<IReadOnlyList<SeededDevice>> SeedAsync(CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({AdvisoryLockKey})", ct);

        await AddMissingTemplatesAsync(ct);

        if (await db.Customers.AnyAsync(ct))
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            logger.LogInformation(
                "Staging seed skipped: the database already contains customers. API keys were logged when it was first seeded; " +
                "issue a new one with 'issue-device-key <deviceCode>' if needed");
            return [];
        }

        var customers = Enumerable.Range(1, CustomerCount)
            .Select(i => new Customer
            {
                Name = $"Staging Customer {i:00}",
                Email = options.Value.CustomerEmail(i),
            })
            .ToList();

        var seeded = new List<SeededDevice>(DeviceCount);
        for (var i = 1; i <= DeviceCount; i++)
        {
            var customer = customers[(i - 1) % CustomerCount];
            var apiKey = DeviceApiKey.Generate();
            var device = new Device
            {
                Name = $"Staging Sensor {i:000}",
                DeviceCode = $"stg-device-{i:000}",
                ApiKeyHash = DeviceApiKey.Hash(apiKey),
            };
            customer.Devices.Add(device);
            seeded.Add(new SeededDevice(device.DeviceCode, customer.Name, apiKey));
        }

        db.Customers.AddRange(customers);

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        logger.LogWarning(
            "Staging seed created {CustomerCount} customers ({FirstCustomerEmail} - {LastCustomerEmail}) and {DeviceCount} devices. " +
            "STAGING ONLY: device API keys follow; they are not stored in plain text and will not be shown again",
            CustomerCount, customers[0].Email, customers[^1].Email, DeviceCount);

        foreach (var device in seeded)
        {
            logger.LogWarning(
                "Staging API key for device {DeviceCode} (customer {CustomerName}): {DeviceApiKey}",
                device.DeviceCode, device.CustomerName, device.ApiKey);
        }

        return seeded;
    }

    /// <summary>Adds the reference templates whose status codes have no template yet; returns how many were added.</summary>
    private async Task<int> AddMissingTemplatesAsync(CancellationToken ct)
    {
        var existing = await db.Templates.Select(t => t.StatusCode).ToListAsync(ct);
        var missing = DefaultTemplates.All.Where(t => !existing.Contains(t.StatusCode)).Select(DefaultTemplates.Copy).ToList();
        db.Templates.AddRange(missing);

        if (missing.Count > 0)
        {
            logger.LogInformation(
                "Staging: added notification templates for status codes {StatusCodes}",
                string.Join(", ", missing.Select(t => t.StatusCode)));
        }

        return missing.Count;
    }
}

public sealed record SeededDevice(string DeviceCode, string CustomerName, string ApiKey)
{
    // Keep the key out of accidental logging of the record itself.
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{DeviceCode} ({CustomerName})");
}
