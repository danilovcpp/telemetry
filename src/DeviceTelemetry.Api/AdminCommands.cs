using DeviceTelemetry.Application.Devices;
using DeviceTelemetry.Infrastructure.Persistence;
using DeviceTelemetry.Infrastructure.Persistence.Seeding;
using Microsoft.EntityFrameworkCore;

namespace DeviceTelemetry.Api;

/// <summary>
/// One-off operational commands run instead of the web host:
/// <c>migrate</c> applies EF Core migrations; <c>issue-device-key &lt;deviceCode&gt;</c> issues a new API key for a device;
/// <c>sync-templates</c> writes the reference notification templates, overwriting templates with the same status codes.
/// </summary>
internal static class AdminCommands
{
    public static bool IsAdminCommand(string[] args) => args is ["migrate", ..] or ["issue-device-key", ..] or ["sync-templates", ..];

    public static async Task<int> RunAsync(IServiceProvider services, string[] args)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        switch (args)
        {
            case ["migrate", ..]:
                await db.Database.MigrateAsync();
                Console.WriteLine("Database migrations applied.");
                return 0;

            case ["issue-device-key", var deviceCode, ..]:
                var device = await db.Devices.SingleOrDefaultAsync(d => d.DeviceCode == deviceCode);
                if (device is null)
                {
                    Console.Error.WriteLine($"Device '{deviceCode}' not found.");
                    return 1;
                }

                var apiKey = DeviceApiKey.Generate();
                device.ApiKeyHash = DeviceApiKey.Hash(apiKey);
                await db.SaveChangesAsync();

                Console.WriteLine($"New API key for device '{deviceCode}' (any previous key is revoked).");
                Console.WriteLine("Store it now; it cannot be shown again. Send it in the X-Device-Key header.");
                Console.WriteLine(apiKey);
                return 0;

            case ["sync-templates", ..]:
                var templates = await db.Templates.ToDictionaryAsync(t => t.StatusCode);
                foreach (var reference in DefaultTemplates.All)
                {
                    if (templates.TryGetValue(reference.StatusCode, out var template))
                    {
                        template.Subject = reference.Subject;
                        template.Body = reference.Body;
                        template.IsHtml = reference.IsHtml;
                        template.IsActive = reference.IsActive;
                    }
                    else
                    {
                        db.Templates.Add(DefaultTemplates.Copy(reference));
                    }
                }

                await db.SaveChangesAsync();
                Console.WriteLine($"Templates written for status codes: {string.Join(", ", DefaultTemplates.All.Select(t => t.StatusCode))}.");

                var unmanaged = templates.Keys.Except(DefaultTemplates.All.Select(t => t.StatusCode)).Order().ToList();
                if (unmanaged.Count > 0)
                    Console.WriteLine($"Left unchanged (not reference templates): {string.Join(", ", unmanaged)}.");
                return 0;

            default:
                Console.Error.WriteLine("Usage: migrate | issue-device-key <deviceCode> | sync-templates");
                return 2;
        }
    }
}
