using DeviceTelemetry.Infrastructure.Outbox;
using DeviceTelemetry.Infrastructure.Telemetry;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DeviceTelemetry.IntegrationTests.DeviceLink;

/// <summary>
/// The real API on a test server, on its own migrated database, with short Device Link timings. Background services that
/// send email or watch for silence are removed so they cannot interfere.
/// </summary>
public sealed class DeviceLinkFactory(string connectionString, IReadOnlyDictionary<string, string?>? settings = null)
    : WebApplicationFactory<Api.Program>
{
    public string DataRoot { get; } = Path.Combine(Path.GetTempPath(), "devicetelemetry-tests", Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        var defaults = new Dictionary<string, string?>
        {
            ["ConnectionStrings:TelemetryDatabase"] = connectionString,
            ["Database:ApplyMigrationsOnStartup"] = "false",
            ["Smtp:Host"] = "localhost",
            ["Smtp:Port"] = "1025",
            ["Smtp:Security"] = "None",
            ["Smtp:FromEmail"] = "telemetry@localhost.test",
            ["DeviceLink:Enabled"] = "true",
            ["DeviceLink:HelloTimeout"] = "00:00:05",
            ["MobileApi:LiveEnabled"] = "true",
            ["MobileApi:HistoryEnabled"] = "true",
            ["Live:StopDelayS"] = "1",
            ["Live:RenewIntervalS"] = "1",
            ["Live:BusyRetryS"] = "1",
            ["Sync:IntervalS"] = "3600",
            ["Sync:BackfillPauseMs"] = "0",
            ["Sync:LivePauseMs"] = "0",
            ["Sync:RequestTimeout"] = "00:00:10",
            ["Storage:DataRoot"] = DataRoot,
            ["RateLimiting:Mobile:PermitLimit"] = "100000",
        };

        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
            defaults[key] = value;

        foreach (var (key, value) in defaults)
            builder.UseSetting(key, value);

        builder.ConfigureTestServices(services =>
        {
            foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IHostedService)
                         && (d.ImplementationType == typeof(OutboxBackgroundService) || d.ImplementationType == typeof(SilenceMonitorBackgroundService)))
                         .ToList())
            {
                services.Remove(descriptor);
            }
        });
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        try
        {
            Directory.Delete(DataRoot, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
