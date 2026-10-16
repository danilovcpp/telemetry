using DeviceTelemetry.Application.Abstractions.Integrations;
using DeviceTelemetry.Application.Abstractions.Persistence;
using DeviceTelemetry.Infrastructure.Integrations.Email;
using DeviceTelemetry.Infrastructure.Outbox;
using DeviceTelemetry.Infrastructure.Persistence;
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

        return services;
    }
}
