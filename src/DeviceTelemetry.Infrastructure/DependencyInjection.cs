using DeviceTelemetry.Application.Abstractions.Integrations;
using DeviceTelemetry.Application.Abstractions.Persistence;
using DeviceTelemetry.Infrastructure.Integrations.Email;
using DeviceTelemetry.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DeviceTelemetry.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddIdentityInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddDbContext<ApplicationDbContext>((sp, options) =>
            options.UseNpgsql(configuration.GetConnectionString("TelemetryDatabase")));

        services.AddScoped<IApplicationDbContext>(provider =>
            provider.GetRequiredService<ApplicationDbContext>());

        services.AddScoped<IEmailSender, EmailSender>();

        return services;
    }
}