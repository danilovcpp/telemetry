using DeviceTelemetry.Application.Devices;
using DeviceTelemetry.Application.Notifications;
using DeviceTelemetry.Infrastructure.Persistence;
using DeviceTelemetry.Infrastructure.Persistence.Seeding;
using Microsoft.EntityFrameworkCore;

namespace DeviceTelemetry.Api;

/// <summary>
/// One-off operational commands run instead of the web host:
/// <c>migrate</c> applies EF Core migrations; <c>issue-device-key &lt;deviceCode&gt;</c> issues a new API key for a device;
/// <c>sync-templates</c> writes the reference notification templates, overwriting those with the same (status code, language);
/// <c>set-customer-language &lt;customerId|all&gt; &lt;en|ru|de|-&gt;</c> sets or clears customers' notification language.
/// </summary>
internal static class AdminCommands
{
    public static bool IsAdminCommand(string[] args) =>
        args is ["migrate", ..] or ["issue-device-key", ..] or ["sync-templates", ..] or ["set-customer-language", ..];

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
                var sync = await TemplateSynchronizer.SyncAsync(db, CancellationToken.None);
                Console.WriteLine(
                    $"Templates written for status codes {string.Join(", ", DefaultTemplates.StatusCodes)} " +
                    $"in languages {string.Join(", ", NotificationLanguages.Supported)}.");

                if (sync.Unmanaged.Count > 0)
                {
                    Console.WriteLine("Left unchanged (not reference templates): " +
                                      string.Join(", ", sync.Unmanaged.Select(k => $"{k.StatusCode} ({k.Language ?? "no language"})")) + ".");
                    Console.WriteLine("Templates without a language are used only when a code has no template in the requested or default language.");
                }

                return 0;

            case ["set-customer-language", var customer, var language, ..]:
                string? value = null;
                if (language != "-")
                {
                    if (!NotificationLanguages.TryNormalize(language, out var normalized))
                    {
                        Console.Error.WriteLine($"Unsupported language '{language}'. Use one of: {string.Join(", ", NotificationLanguages.Supported)}, or - to clear.");
                        return 2;
                    }

                    value = normalized;
                }

                var customers = customer == "all"
                    ? db.Customers
                    : int.TryParse(customer, out var customerId) ? db.Customers.Where(c => c.Id == customerId) : null;
                if (customers is null)
                {
                    Console.Error.WriteLine("Customer must be a numeric id or 'all'.");
                    return 2;
                }

                var updated = await customers.ExecuteUpdateAsync(s => s.SetProperty(c => c.Language, value));
                if (updated == 0)
                {
                    Console.Error.WriteLine($"Customer '{customer}' not found.");
                    return 1;
                }

                Console.WriteLine($"Language of {updated} customer(s) set to {value ?? $"the default ({NotificationLanguages.Default})"}.");
                return 0;

            default:
                Console.Error.WriteLine("Usage: migrate | issue-device-key <deviceCode> | sync-templates | set-customer-language <customerId|all> <en|ru|de|->");
                return 2;
        }
    }
}
