using System.Diagnostics;
using System.Text.Json;
using DeviceTelemetry.Application.Abstractions.Integrations;
using DeviceTelemetry.Application.Outbox;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DeviceTelemetry.Infrastructure.Outbox;

/// <summary>
/// Claims one batch of due outbox messages and delivers them sequentially.
/// <para>
/// Delivery is at-least-once: if the provider accepts an email and the process dies before the message is marked
/// as sent, the lease expires and the email is sent again. The outbox id is passed as the provider idempotency key
/// (SMTP Message-ID) so such duplicates can be recognised downstream.
/// </para>
/// </summary>
public sealed class OutboxProcessor(
    IOutboxStore store,
    IEmailSender emailSender,
    IOptions<OutboxOptions> options,
    TimeProvider timeProvider,
    ILogger<OutboxProcessor> logger)
{
    private readonly OutboxOptions _options = options.Value;

    /// <returns>The number of messages claimed.</returns>
    public async Task<int> ProcessBatchAsync(string workerId, CancellationToken ct)
    {
        var batch = await store.ClaimBatchAsync(
            workerId, _options.BatchSize, timeProvider.GetUtcNow().UtcDateTime, _options.LockDuration, ct);

        for (var i = 0; i < batch.Count; i++)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                await ProcessAsync(batch[i], workerId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutdown: hand the current and all remaining messages back without counting the attempt.
                for (var j = i; j < batch.Count; j++)
                    await store.ReleaseAsync(batch[j].Id, workerId, CancellationToken.None);

                logger.LogInformation("Outbox processing cancelled; released {Count} message(s)", batch.Count - i);
                throw;
            }
        }

        return batch.Count;
    }

    private async Task ProcessAsync(OutboxMessage message, string workerId, CancellationToken ct)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["OutboxMessageId"] = message.Id,
            ["EventId"] = message.TelemetryEventId,
            ["Attempt"] = message.Attempts,
        });

        if (message.Attempts > _options.MaxAttempts)
        {
            // The lease of the final attempt expired, i.e. a worker died while sending it.
            await FailAsync(message, workerId, "Attempt limit reached: the last attempt did not complete (worker stopped or lease expired).");
            return;
        }

        if (message.Type != OutboxMessageTypes.Email)
        {
            await FailAsync(message, workerId, $"Unsupported outbox message type '{message.Type}'.");
            return;
        }

        EmailMessage email;
        try
        {
            email = message.ReadEmail();
        }
        catch (JsonException ex)
        {
            await FailAsync(message, workerId, $"Content cannot be deserialized: {ex.Message}");
            return;
        }

        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            await emailSender.SendAsync(email, message.Id.ToString("N"), ct);
        }
        catch (EmailSendException ex) when (!ex.IsTransient)
        {
            logger.LogError(ex, "Outbox message {OutboxMessageId} permanently rejected by the email provider", message.Id);
            await FailAsync(message, workerId, ex.Message);
            return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Transient provider errors and unexpected failures are retried with backoff up to MaxAttempts;
            // the error is recorded on the message and logged, never swallowed.
            await RetryOrFailAsync(message, workerId, ex);
            return;
        }

        var elapsed = Stopwatch.GetElapsedTime(startedAt);

        // Bookkeeping after a successful send must not be skipped because shutdown was requested meanwhile.
        if (await store.MarkSentAsync(message.Id, workerId, timeProvider.GetUtcNow().UtcDateTime, CancellationToken.None))
        {
            logger.LogInformation(
                "Outbox message {OutboxMessageId} for event {EventId} sent on attempt {Attempt} in {ElapsedMs} ms",
                message.Id, message.TelemetryEventId, message.Attempts, (long)elapsed.TotalMilliseconds);
        }
        else
        {
            logger.LogWarning(
                "Outbox message {OutboxMessageId} was sent but its lease had been lost; another worker may send it again",
                message.Id);
        }
    }

    private async Task RetryOrFailAsync(OutboxMessage message, string workerId, Exception ex)
    {
        var error = $"{ex.GetType().Name}: {ex.Message}";

        if (message.Attempts >= _options.MaxAttempts)
        {
            logger.LogError(ex,
                "Outbox message {OutboxMessageId} for event {EventId} failed on final attempt {Attempt}; giving up",
                message.Id, message.TelemetryEventId, message.Attempts);
            await FailAsync(message, workerId, $"Attempt limit reached. Last error: {error}");
            return;
        }

        var delay = GetRetryDelay(message.Attempts, _options);
        var nextAttemptAtUtc = timeProvider.GetUtcNow().UtcDateTime + delay;

        logger.LogWarning(ex,
            "Outbox message {OutboxMessageId} for event {EventId} failed on attempt {Attempt} ({ErrorType}); retrying at {NextAttemptAtUtc}",
            message.Id, message.TelemetryEventId, message.Attempts, ex.GetType().Name, nextAttemptAtUtc);

        await store.ScheduleRetryAsync(message.Id, workerId, nextAttemptAtUtc, error, CancellationToken.None);
    }

    private async Task FailAsync(OutboxMessage message, string workerId, string error)
    {
        logger.LogError(
            "Outbox message {OutboxMessageId} for event {EventId} marked as failed after {Attempt} attempt(s): {Error}",
            message.Id, message.TelemetryEventId, message.Attempts, error);

        await store.MarkFailedAsync(message.Id, workerId, error, CancellationToken.None);
    }

    /// <summary>Exponential backoff: base, 2×base, 4×base, … capped at <see cref="OutboxOptions.MaxRetryDelay"/>.</summary>
    public static TimeSpan GetRetryDelay(int failedAttempts, OutboxOptions options)
    {
        var exponent = Math.Clamp(failedAttempts - 1, 0, 30);
        var delay = options.BaseRetryDelay * Math.Pow(2, exponent);
        return delay < options.MaxRetryDelay ? delay : options.MaxRetryDelay;
    }
}
