using DeviceTelemetry.Application.Outbox;
using DeviceTelemetry.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DeviceTelemetry.Infrastructure.Outbox;

public sealed class PostgresOutboxStore(ApplicationDbContext context) : IOutboxStore
{
    public async Task<IReadOnlyList<OutboxMessage>> ClaimBatchAsync(
        string workerId, int batchSize, DateTime nowUtc, TimeSpan lockDuration, CancellationToken ct)
    {
        var lockedUntilUtc = nowUtc + lockDuration;

        // Row locks taken by FOR UPDATE SKIP LOCKED last only for this single statement; afterwards the lease
        // (Status = Processing, LockedUntilUtc) keeps other workers away, so no transaction is held open while sending.
        return await context.OutboxMessages
            .FromSql($"""
                UPDATE "OutboxMessages" AS m
                SET "Status" = 'Processing',
                    "Attempts" = m."Attempts" + 1,
                    "LockedBy" = {workerId},
                    "LockedUntilUtc" = {lockedUntilUtc}
                WHERE m."Id" IN (
                    SELECT c."Id"
                    FROM "OutboxMessages" AS c
                    WHERE (c."Status" = 'Pending' AND c."NextAttemptAtUtc" <= {nowUtc})
                       OR (c."Status" = 'Processing' AND c."LockedUntilUtc" <= {nowUtc})
                    ORDER BY c."NextAttemptAtUtc"
                    LIMIT {batchSize}
                    FOR UPDATE SKIP LOCKED)
                RETURNING m.*
                """)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public async Task<bool> MarkSentAsync(Guid id, string workerId, DateTime nowUtc, CancellationToken ct) =>
        await Owned(id, workerId).ExecuteUpdateAsync(s => s
            .SetProperty(m => m.Status, OutboxMessageStatus.Sent)
            .SetProperty(m => m.ProcessedAtUtc, nowUtc)
            .SetProperty(m => m.LockedBy, (string?)null)
            .SetProperty(m => m.LockedUntilUtc, (DateTime?)null), ct) == 1;

    public async Task<bool> ScheduleRetryAsync(Guid id, string workerId, DateTime nextAttemptAtUtc, string error, CancellationToken ct) =>
        await Owned(id, workerId).ExecuteUpdateAsync(s => s
            .SetProperty(m => m.Status, OutboxMessageStatus.Pending)
            .SetProperty(m => m.NextAttemptAtUtc, nextAttemptAtUtc)
            .SetProperty(m => m.LastError, Truncate(error))
            .SetProperty(m => m.LockedBy, (string?)null)
            .SetProperty(m => m.LockedUntilUtc, (DateTime?)null), ct) == 1;

    public async Task<bool> MarkFailedAsync(Guid id, string workerId, string error, CancellationToken ct) =>
        await Owned(id, workerId).ExecuteUpdateAsync(s => s
            .SetProperty(m => m.Status, OutboxMessageStatus.Failed)
            .SetProperty(m => m.LastError, Truncate(error))
            .SetProperty(m => m.LockedBy, (string?)null)
            .SetProperty(m => m.LockedUntilUtc, (DateTime?)null), ct) == 1;

    public async Task<bool> ReleaseAsync(Guid id, string workerId, CancellationToken ct) =>
        await Owned(id, workerId).ExecuteUpdateAsync(s => s
            .SetProperty(m => m.Status, OutboxMessageStatus.Pending)
            .SetProperty(m => m.Attempts, m => m.Attempts - 1)
            .SetProperty(m => m.LockedBy, (string?)null)
            .SetProperty(m => m.LockedUntilUtc, (DateTime?)null), ct) == 1;

    private IQueryable<OutboxMessage> Owned(Guid id, string workerId) =>
        context.OutboxMessages.Where(m => m.Id == id && m.Status == OutboxMessageStatus.Processing && m.LockedBy == workerId);

    private static string Truncate(string error) =>
        error.Length <= OutboxMessage.LastErrorMaxLength ? error : error[..OutboxMessage.LastErrorMaxLength];
}
