using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DeviceTelemetry.Infrastructure.Outbox;

/// <summary>
/// Polls the outbox and runs <see cref="OutboxProcessor"/> in a fresh DI scope (and therefore a fresh DbContext) per batch.
/// Each application instance runs one sequential worker, which bounds the number of concurrent SMTP connections.
/// </summary>
public sealed class OutboxBackgroundService(
    IServiceScopeFactory scopeFactory,
    IOptions<OutboxOptions> options,
    TimeProvider timeProvider,
    ILogger<OutboxBackgroundService> logger) : BackgroundService
{
    private readonly string _workerId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        logger.LogInformation("Outbox worker {WorkerId} started", _workerId);

        while (!stoppingToken.IsCancellationRequested)
        {
            var claimed = 0;
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<OutboxProcessor>();
                claimed = await processor.ProcessBatchAsync(_workerId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // E.g. the database is unreachable. Keep the worker alive and try again after the polling interval.
                logger.LogError(ex, "Outbox worker {WorkerId} failed to process a batch", _workerId);
            }

            // A full batch suggests more work is waiting, so poll again immediately.
            if (claimed >= settings.BatchSize)
                continue;

            try
            {
                await Task.Delay(settings.PollingInterval, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogInformation("Outbox worker {WorkerId} stopped", _workerId);
    }
}
