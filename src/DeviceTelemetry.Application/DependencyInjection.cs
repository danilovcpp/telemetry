using System.Reflection;
using DeviceTelemetry.Application.Common;
using DeviceTelemetry.Application.Devices;
using DeviceTelemetry.Application.Notifications;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DeviceTelemetry.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        var assembly = Assembly.GetExecutingAssembly();

        services.AddValidatorsFromAssembly(assembly, ServiceLifetime.Singleton);

        // Register all command/query handlers.
        RegisterHandlers(services, assembly, typeof(ICommandHandler<,>));
        RegisterHandlers(services, assembly, typeof(IQueryHandler<,>));

        services.AddSingleton<EmailTemplateRenderer>();
        services.AddScoped<TelemetryNotifier>();
        services.AddScoped<DeviceContactRecorder>();
        services.TryAddSingleton(TimeProvider.System);

        return services;
    }

    private static void RegisterHandlers(IServiceCollection services, Assembly asm, Type openInterface)
    {
        var implementations =
            from t in asm.GetTypes()
            where t is { IsAbstract: false, IsInterface: false }
            from i in t.GetInterfaces()
            where i.IsGenericType && i.GetGenericTypeDefinition() == openInterface
            select (Service: i, Implementation: t);

        foreach (var (service, impl) in implementations)
            services.AddScoped(service, impl);
    }
}