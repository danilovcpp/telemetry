using DeviceTelemetry.Application.Outbox;

namespace DeviceTelemetry.Infrastructure.Outbox;

/// <summary>
/// Outbox storage operations used by <see cref="OutboxProcessor"/>. Separated from the processor so the
/// delivery/retry logic can be unit-tested; the PostgreSQL implementation is covered by integration tests.
/// All state transitions after a claim apply only while <paramref name="workerId"/> still holds the lease and
/// return <c>false</c> if it was lost.
/// </summary>
public interface IOutboxStore
{
    /// <summary>
    /// Atomically claims up to <paramref name="batchSize"/> due messages (pending and due, or processing with an
    /// expired lease), sets them to <see cref="OutboxMessageStatus.Processing"/> under a lease and increments their attempt count.
    /// Concurrent callers never receive the same message.
    /// </summary>
    Task<IReadOnlyList<OutboxMessage>> ClaimBatchAsync(
        string workerId, int batchSize, DateTime nowUtc, TimeSpan lockDuration, CancellationToken ct);

    Task<bool> MarkSentAsync(Guid id, string workerId, DateTime nowUtc, CancellationToken ct);

    Task<bool> ScheduleRetryAsync(Guid id, string workerId, DateTime nextAttemptAtUtc, string error, CancellationToken ct);

    Task<bool> MarkFailedAsync(Guid id, string workerId, string error, CancellationToken ct);

    /// <summary>Returns a claimed message to the queue without counting the attempt (used on shutdown).</summary>
    Task<bool> ReleaseAsync(Guid id, string workerId, CancellationToken ct);
}
