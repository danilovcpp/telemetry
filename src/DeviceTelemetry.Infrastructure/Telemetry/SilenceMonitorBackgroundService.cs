using DeviceTelemetry.Application.Common;
using DeviceTelemetry.Application.Telemetry;
using DeviceTelemetry.Application.Telemetry.Commands.DetectSilentDevices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DeviceTelemetry.Infrastructure.Telemetry;

/// <summary>
/// Runs <see cref="DetectSilentDevicesCommand"/> every <see cref="SilenceMonitorOptions.CheckInterval"/>, in a fresh DI
/// scope each time. The command itself is safe against concurrent instances.
/// </summary>
public sealed class SilenceMonitorBackgroundService(
    IServiceScopeFactory scopeFactory,
    IOptions<SilenceMonitorOptions> options,
    TimeProvider timeProvider,
    ILogger<SilenceMonitorBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<DetectSilentDevicesCommand, int>>();
                await handler.HandleAsync(new DetectSilentDevicesCommand(), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // E.g. the database is unreachable. Keep the monitor alive and try again on the next tick.
                logger.LogError(ex, "Silence check failed");
            }

            try
            {
                await Task.Delay(options.Value.CheckInterval, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
