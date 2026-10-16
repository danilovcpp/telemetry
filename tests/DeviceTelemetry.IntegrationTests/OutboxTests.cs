using DeviceTelemetry.Application.Abstractions.Integrations;
using DeviceTelemetry.Application.Outbox;
using DeviceTelemetry.Infrastructure.Outbox;
using DeviceTelemetry.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace DeviceTelemetry.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class OutboxTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    // Tests in a collection run sequentially, so clearing the table isolates the claim tests.
    public async Task InitializeAsync()
    {
        await using var db = postgres.CreateContext();
        await db.OutboxMessages.ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<List<Guid>> AddDueMessagesAsync(int count, DateTime nowUtc)
    {
        await using var db = postgres.CreateContext();
        var messages = Enumerable.Range(0, count)
            .Select(i => OutboxMessage.ForEmail(
                Guid.CreateVersion7(), new EmailMessage($"user{i}@example.com", "S", "B", false), null, nowUtc))
            .ToList();
        db.OutboxMessages.AddRange(messages);
        await db.SaveChangesAsync();
        return messages.Select(m => m.Id).ToList();
    }

    private async Task<IReadOnlyList<OutboxMessage>> ClaimAsync(string worker, int batchSize, DateTime nowUtc)
    {
        await using var db = postgres.CreateContext();
        return await new PostgresOutboxStore(db).ClaimBatchAsync(worker, batchSize, nowUtc, Lease, CancellationToken.None);
    }

    [Fact]
    public async Task Concurrent_workers_never_claim_the_same_message()
    {
        var now = DateTime.UtcNow;
        var ids = await AddDueMessagesAsync(200, now);

        async Task<List<Guid>> Drain(string worker)
        {
            var claimed = new List<Guid>();
            while (true)
            {
                var batch = await ClaimAsync(worker, batchSize: 7, now);
                if (batch.Count == 0)
                    return claimed;
                claimed.AddRange(batch.Select(m => m.Id));
            }
        }

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => Task.Run(() => Drain($"worker-{i}"))));
        var all = results.SelectMany(r => r).ToList();

        Assert.Equal(ids.Count, all.Count);
        Assert.Equal(ids.Order(), all.Order());

        await using var verify = postgres.CreateContext();
        Assert.True(await verify.OutboxMessages.AllAsync(m => m.Status == OutboxMessageStatus.Processing && m.Attempts == 1));
    }

    [Fact]
    public async Task Leased_message_is_reclaimed_only_after_the_lease_expires()
    {
        var now = DateTime.UtcNow;
        var id = (await AddDueMessagesAsync(1, now)).Single();

        Assert.Single(await ClaimAsync("worker-a", 10, now));
        Assert.Empty(await ClaimAsync("worker-b", 10, now + Lease - TimeSpan.FromSeconds(1)));

        var reclaimed = Assert.Single(await ClaimAsync("worker-b", 10, now + Lease));
        Assert.Equal(id, reclaimed.Id);
        Assert.Equal(2, reclaimed.Attempts);
        Assert.Equal("worker-b", reclaimed.LockedBy);

        // The original worker has lost the lease and can no longer change the message.
        await using var db = postgres.CreateContext();
        Assert.False(await new PostgresOutboxStore(db).MarkSentAsync(id, "worker-a", now, CancellationToken.None));
        Assert.True(await new PostgresOutboxStore(db).MarkSentAsync(id, "worker-b", now, CancellationToken.None));
    }

    [Fact]
    public async Task Processor_persists_retry_state_and_later_marks_the_message_sent()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var id = (await AddDueMessagesAsync(1, time.UtcNow)).Single();
        var options = new OutboxOptions { BaseRetryDelay = TimeSpan.FromMinutes(1), MaxAttempts = 3 };
        var sender = new Mock<IEmailSender>();
        sender.SetupSequence(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new EmailSendException("SMTP server replied 451", isTransient: true))
            .Returns(Task.CompletedTask);

        async Task RunAsync()
        {
            await using var db = postgres.CreateContext(time);
            var processor = new OutboxProcessor(
                new PostgresOutboxStore(db), sender.Object, Options.Create(options), time, NullLogger<OutboxProcessor>.Instance);
            await processor.ProcessBatchAsync("worker", CancellationToken.None);
        }

        await RunAsync();
        await using (var db = postgres.CreateContext())
        {
            var failed = await db.OutboxMessages.SingleAsync(m => m.Id == id);
            Assert.Equal(OutboxMessageStatus.Pending, failed.Status);
            Assert.Equal(1, failed.Attempts);
            Assert.Contains("451", failed.LastError);
            Assert.Equal(time.UtcNow + options.BaseRetryDelay, failed.NextAttemptAtUtc, TimeSpan.FromMilliseconds(1));
        }

        time.Advance(options.BaseRetryDelay);
        await RunAsync();
        await using (var db = postgres.CreateContext())
        {
            var sent = await db.OutboxMessages.SingleAsync(m => m.Id == id);
            Assert.Equal(OutboxMessageStatus.Sent, sent.Status);
            Assert.Equal(2, sent.Attempts);
            Assert.NotNull(sent.ProcessedAtUtc);
        }

        sender.Verify(s => s.SendAsync(It.IsAny<EmailMessage>(), id.ToString("N"), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }
}
