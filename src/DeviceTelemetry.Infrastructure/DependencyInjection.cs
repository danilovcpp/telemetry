using DeviceTelemetry.Application.Abstractions.DeviceLink;
using DeviceTelemetry.Application.Abstractions.Integrations;
using DeviceTelemetry.Application.Abstractions.Persistence;
using DeviceTelemetry.Application.Mobile;
using DeviceTelemetry.Infrastructure.DeviceLink;
using DeviceTelemetry.Infrastructure.Integrations.Email;
using DeviceTelemetry.Infrastructure.Outbox;
using DeviceTelemetry.Infrastructure.Persistence;
using DeviceTelemetry.Infrastructure.Persistence.Seeding;
using DeviceTelemetry.Infrastructure.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DeviceTelemetry.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringName = "TelemetryDatabase";

    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException($"Connection string 'ConnectionStrings:{ConnectionStringName}' is not configured.");

        services.TryAddSingleton(TimeProvider.System);

        // Scoped: one DbContext per HTTP request or per outbox batch, never shared between threads.
        services.AddDbContext<ApplicationDbContext>(options => options.UseNpgsql(connectionString));
        services.AddScoped<IApplicationDbContext>(provider => provider.GetRequiredService<ApplicationDbContext>());
        services.AddOptions<StagingSeedOptions>()
            .Bind(configuration.GetSection(StagingSeedOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddScoped<StagingDataSeeder>();

        services.AddOptions<SmtpOptions>()
            .Bind(configuration.GetSection(SmtpOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<IEmailSender, EmailSender>();

        services.AddOptions<OutboxOptions>()
            .Bind(configuration.GetSection(OutboxOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddScoped<IOutboxStore, PostgresOutboxStore>();
        services.AddScoped<OutboxProcessor>();
        services.AddHostedService<OutboxBackgroundService>();
        services.AddHostedService<SilenceMonitorBackgroundService>();

        services.AddDeviceLink(configuration);

        return services;
    }

    /// <summary>Device Link (live values, data file copies) and the switches of the mobile features built on it.</summary>
    private static void AddDeviceLink(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<DeviceLinkOptions>()
            .Bind(configuration.GetSection(DeviceLinkOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<LiveOptions>()
            .Bind(configuration.GetSection(LiveOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<SyncOptions>()
            .Bind(configuration.GetSection(SyncOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<StorageOptions>()
            .Bind(configuration.GetSection(StorageOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<MobileFeatureOptions>().Configure(options =>
        {
            options.DeviceLinkEnabled = configuration.GetValue<bool>("DeviceLink:Enabled");
            options.LiveEnabled = configuration.GetValue<bool>("MobileApi:LiveEnabled");
            options.HistoryEnabled = configuration.GetValue<bool>("MobileApi:HistoryEnabled");
        });

        services.AddSingleton<DeviceLinkRegistry>();
        services.AddSingleton<IDeviceLinkStatus>(provider => provider.GetRequiredService<DeviceLinkRegistry>());
        services.AddSingleton<LogFileStorage>();
        services.AddSingleton<ILogFileStorage>(provider => provider.GetRequiredService<LogFileStorage>());
        services.AddSingleton<LiveHub>();
        services.AddSingleton<LogSynchronizer>();
        services.AddSingleton<DeviceLinkHandler>();
    }
}
