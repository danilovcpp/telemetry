using System.Text.Json.Serialization;
using DeviceTelemetry.Api.Http;
using DeviceTelemetry.Application;
using DeviceTelemetry.Application.Telemetry;
using DeviceTelemetry.Infrastructure;
using DeviceTelemetry.Infrastructure.Persistence;
using DeviceTelemetry.Infrastructure.Persistence.Seeding;
using Microsoft.EntityFrameworkCore;

namespace DeviceTelemetry.Api;

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Requests larger than this are rejected with 413 before the body is read.
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize =
            builder.Configuration.GetValue<long?>("Kestrel:Limits:MaxRequestBodySize") ?? 32_768);

        builder.Services
            .AddControllers()
            .AddJsonOptions(options =>
            {
                options.JsonSerializerOptions.MaxDepth = 32;
                // Numbers must be JSON numbers ("statusCode": 100001, not "100001"); also keeps OpenAPI number types exact.
                options.JsonSerializerOptions.NumberHandling = JsonNumberHandling.Strict;
            });
        // The OpenAPI generator reads these (not the MVC ones): keep them identical so the document matches the wire format.
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict);
        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<BadHttpRequestExceptionHandler>();
        builder.Services.AddExceptionHandler<DatabaseUnavailableExceptionHandler>();

        // Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
        builder.Services.AddOpenApi(options => options.AddSchemaTransformer<RawJsonSchemaTransformer>());

        builder.Services.AddOptions<TelemetryOptions>()
            .Bind(builder.Configuration.GetSection(TelemetryOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        builder.Services.AddApplication();
        builder.Services.AddInfrastructure(builder.Configuration);
        builder.Services.AddTelemetryRateLimiting(builder.Configuration);

        var app = builder.Build();

        if (AdminCommands.IsAdminCommand(args))
            return await AdminCommands.RunAsync(app.Services, args);

        // Convenience for Development and Staging; in production run `migrate` (or a migration bundle) as a deployment step.
        if (app.Configuration.GetValue<bool>("Database:ApplyMigrationsOnStartup"))
        {
            await using var scope = app.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.MigrateAsync();
        }

        // Test data with logged API keys: Staging only, and only into an empty database.
        if (app.Environment.IsStaging())
        {
            await using var scope = app.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<StagingDataSeeder>().SeedAsync(app.Lifetime.ApplicationStopping);
        }

        // Unhandled exceptions become problem responses without internal details.
        // HTTPS is expected to be terminated in front of the service; plain HTTP is not redirected because
        // devices would resend the API key over HTTP before following a redirect.
        app.UseExceptionHandler();
        app.UseStatusCodePages();

        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
        }

        app.UseRateLimiter();
        app.MapControllers();

        await app.RunAsync();
        return 0;
    }
}
