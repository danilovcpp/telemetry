using DeviceTelemetry.Application.Devices;
using DeviceTelemetry.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DeviceTelemetry.Api;

/// <summary>
/// One-off operational commands run instead of the web host:
/// <c>migrate</c> applies EF Core migrations; <c>issue-device-key &lt;deviceCode&gt;</c> issues a new API key for a device.
/// </summary>
internal static class AdminCommands
{
    public static bool IsAdminCommand(string[] args) => args is ["migrate", ..] or ["issue-device-key", ..];

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

            default:
                Console.Error.WriteLine("Usage: migrate | issue-device-key <deviceCode>");
                return 2;
        }
    }
}
