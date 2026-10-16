using DeviceTelemetry.Application.Outbox;
using DeviceTelemetry.Infrastructure.Outbox;

namespace DeviceTelemetry.UnitTests.Outbox;

/// <summary>
/// Mirrors the claim/lease semantics of <see cref="PostgresOutboxStore"/> over a list.
/// The real store, including concurrency, is covered by the PostgreSQL integration tests.
/// </summary>
internal sealed class InMemoryOutboxStore : IOutboxStore
{
    public List<OutboxMessage> Messages { get; } = [];

    public Task<IReadOnlyList<OutboxMessage>> ClaimBatchAsync(
        string workerId, int batchSize, DateTime nowUtc, TimeSpan lockDuration, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var claimed = Messages
            .Where(m => (m.Status == OutboxMessageStatus.Pending && m.NextAttemptAtUtc <= nowUtc)
                        || (m.Status == OutboxMessageStatus.Processing && m.LockedUntilUtc <= nowUtc))
            .OrderBy(m => m.NextAttemptAtUtc)
            .Take(batchSize)
            .ToList();

        foreach (var m in claimed)
        {
            m.Status = OutboxMessageStatus.Processing;
            m.Attempts++;
            m.LockedBy = workerId;
            m.LockedUntilUtc = nowUtc + lockDuration;
        }

        return Task.FromResult<IReadOnlyList<OutboxMessage>>(claimed.Select(Copy).ToList());
    }

    public Task<bool> MarkSentAsync(Guid id, string workerId, DateTime nowUtc, CancellationToken ct) =>
        Update(id, workerId, m =>
        {
            m.Status = OutboxMessageStatus.Sent;
            m.ProcessedAtUtc = nowUtc;
        });

    public Task<bool> ScheduleRetryAsync(Guid id, string workerId, DateTime nextAttemptAtUtc, string error, CancellationToken ct) =>
        Update(id, workerId, m =>
        {
            m.Status = OutboxMessageStatus.Pending;
            m.NextAttemptAtUtc = nextAttemptAtUtc;
            m.LastError = error;
        });

    public Task<bool> MarkFailedAsync(Guid id, string workerId, string error, CancellationToken ct) =>
        Update(id, workerId, m =>
        {
            m.Status = OutboxMessageStatus.Failed;
            m.LastError = error;
        });

    public Task<bool> ReleaseAsync(Guid id, string workerId, CancellationToken ct) =>
        Update(id, workerId, m =>
        {
            m.Status = OutboxMessageStatus.Pending;
            m.Attempts--;
        });

    private Task<bool> Update(Guid id, string workerId, Action<OutboxMessage> apply)
    {
        var message = Messages.SingleOrDefault(m =>
            m.Id == id && m.Status == OutboxMessageStatus.Processing && m.LockedBy == workerId);
        if (message is null)
            return Task.FromResult(false);

        apply(message);
        message.LockedBy = null;
        message.LockedUntilUtc = null;
        return Task.FromResult(true);
    }

    private static OutboxMessage Copy(OutboxMessage m) => new()
    {
        Id = m.Id,
        Type = m.Type,
        Content = m.Content,
        TelemetryEventId = m.TelemetryEventId,
        Status = m.Status,
        Attempts = m.Attempts,
        CreatedAtUtc = m.CreatedAtUtc,
        NextAttemptAtUtc = m.NextAttemptAtUtc,
        LockedUntilUtc = m.LockedUntilUtc,
        LockedBy = m.LockedBy,
        ProcessedAtUtc = m.ProcessedAtUtc,
        LastError = m.LastError,
    };
}
