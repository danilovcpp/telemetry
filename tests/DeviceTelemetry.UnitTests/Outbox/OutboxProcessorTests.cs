using System.Net.Sockets;
using DeviceTelemetry.Application.Abstractions.Integrations;
using DeviceTelemetry.Application.Outbox;
using DeviceTelemetry.Infrastructure.Outbox;
using DeviceTelemetry.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace DeviceTelemetry.UnitTests.Outbox;

public sealed class OutboxProcessorTests
{
    private const string Worker = "worker-1";

    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));
    private readonly InMemoryOutboxStore _store = new();
    private readonly Mock<IEmailSender> _sender = new();
    private readonly OutboxOptions _options = new()
    {
        BatchSize = 10,
        MaxAttempts = 3,
        BaseRetryDelay = TimeSpan.FromSeconds(30),
        MaxRetryDelay = TimeSpan.FromMinutes(10),
        LockDuration = TimeSpan.FromMinutes(5),
    };

    private OutboxProcessor CreateProcessor() =>
        new(_store, _sender.Object, Options.Create(_options), _time, NullLogger<OutboxProcessor>.Instance);

    private OutboxMessage AddEmail(int attempts = 0)
    {
        var message = OutboxMessage.ForEmail(
            Guid.CreateVersion7(), new EmailMessage("owner@example.com", "Subject", "Body", IsHtml: false), Guid.NewGuid(), _time.UtcNow);
        message.Attempts = attempts;
        _store.Messages.Add(message);
        return message;
    }

    [Fact]
    public async Task Successful_send_marks_message_as_sent()
    {
        var message = AddEmail();

        var claimed = await CreateProcessor().ProcessBatchAsync(Worker, CancellationToken.None);

        Assert.Equal(1, claimed);
        Assert.Equal(OutboxMessageStatus.Sent, message.Status);
        Assert.Equal(1, message.Attempts);
        Assert.Equal(_time.UtcNow, message.ProcessedAtUtc);
        Assert.Null(message.LockedBy);
        _sender.Verify(s => s.SendAsync(
            It.Is<EmailMessage>(e => e.To == "owner@example.com" && e.Subject == "Subject" && e.Body == "Body"),
            message.Id.ToString("N"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Transient_error_schedules_a_retry_with_backoff_and_records_the_error()
    {
        var message = AddEmail();
        _sender.Setup(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new EmailSendException("SMTP server replied 421", isTransient: true));

        await CreateProcessor().ProcessBatchAsync(Worker, CancellationToken.None);

        Assert.Equal(OutboxMessageStatus.Pending, message.Status);
        Assert.Equal(1, message.Attempts);
        Assert.Equal(_time.UtcNow + _options.BaseRetryDelay, message.NextAttemptAtUtc);
        Assert.Contains("421", message.LastError);
        Assert.Null(message.LockedBy);
    }

    [Fact]
    public async Task Unexpected_error_is_retried_like_a_transient_one()
    {
        var message = AddEmail();
        _sender.Setup(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new SocketException());

        await CreateProcessor().ProcessBatchAsync(Worker, CancellationToken.None);

        Assert.Equal(OutboxMessageStatus.Pending, message.Status);
        Assert.StartsWith(nameof(SocketException), message.LastError);
    }

    [Fact]
    public async Task Retry_is_not_attempted_before_its_due_time_and_succeeds_afterwards()
    {
        var message = AddEmail();
        _sender.SetupSequence(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new EmailSendException("timeout", isTransient: true))
            .Returns(Task.CompletedTask);
        var processor = CreateProcessor();

        await processor.ProcessBatchAsync(Worker, CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(0, await processor.ProcessBatchAsync(Worker, CancellationToken.None));

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, await processor.ProcessBatchAsync(Worker, CancellationToken.None));
        Assert.Equal(OutboxMessageStatus.Sent, message.Status);
        Assert.Equal(2, message.Attempts);
    }

    [Fact]
    public async Task Message_fails_permanently_after_max_attempts()
    {
        var message = AddEmail();
        _sender.Setup(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new EmailSendException("unavailable", isTransient: true));
        var processor = CreateProcessor();

        for (var i = 0; i < 10; i++)
        {
            await processor.ProcessBatchAsync(Worker, CancellationToken.None);
            _time.Advance(_options.MaxRetryDelay);
        }

        Assert.Equal(OutboxMessageStatus.Failed, message.Status);
        Assert.Equal(_options.MaxAttempts, message.Attempts);
        Assert.StartsWith("Attempt limit reached", message.LastError);
        _sender.Verify(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Exactly(_options.MaxAttempts));
    }

    [Fact]
    public async Task Permanent_error_fails_without_retrying()
    {
        var message = AddEmail();
        _sender.Setup(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new EmailSendException("SMTP server replied 550: mailbox unavailable", isTransient: false));

        await CreateProcessor().ProcessBatchAsync(Worker, CancellationToken.None);

        Assert.Equal(OutboxMessageStatus.Failed, message.Status);
        Assert.Contains("550", message.LastError);
    }

    [Fact]
    public async Task Message_whose_last_attempt_never_completed_is_failed_without_sending()
    {
        // A worker died during the final attempt: the message is still Processing with an expired lease.
        var message = AddEmail(attempts: _options.MaxAttempts);
        message.Status = OutboxMessageStatus.Processing;
        message.LockedBy = "dead-worker";
        message.LockedUntilUtc = _time.UtcNow.AddSeconds(-1);

        await CreateProcessor().ProcessBatchAsync(Worker, CancellationToken.None);

        Assert.Equal(OutboxMessageStatus.Failed, message.Status);
        _sender.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Undeserializable_content_fails_the_message()
    {
        var message = AddEmail();
        message.Content = "\"not an email\"";

        await CreateProcessor().ProcessBatchAsync(Worker, CancellationToken.None);

        Assert.Equal(OutboxMessageStatus.Failed, message.Status);
        Assert.StartsWith("Content cannot be deserialized", message.LastError);
    }

    [Fact]
    public async Task Cancellation_releases_current_and_remaining_messages_without_counting_attempts()
    {
        var first = AddEmail();
        var second = AddEmail();
        using var cts = new CancellationTokenSource();
        _sender.Setup(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<EmailMessage, string, CancellationToken>((_, _, ct) =>
            {
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateProcessor().ProcessBatchAsync(Worker, cts.Token));

        Assert.All([first, second], m =>
        {
            Assert.Equal(OutboxMessageStatus.Pending, m.Status);
            Assert.Equal(0, m.Attempts);
            Assert.Null(m.LockedBy);
        });
        _sender.Verify(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Sent_message_is_recorded_even_if_shutdown_is_requested_after_sending()
    {
        var message = AddEmail();
        using var cts = new CancellationTokenSource();
        _sender.Setup(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => cts.Cancel())
            .Returns(Task.CompletedTask);

        await CreateProcessor().ProcessBatchAsync(Worker, cts.Token);

        Assert.Equal(OutboxMessageStatus.Sent, message.Status);
    }

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(3, 120)]
    [InlineData(5, 480)]
    [InlineData(6, 600)] // capped at MaxRetryDelay
    [InlineData(50, 600)]
    public void Retry_delay_grows_exponentially_up_to_the_cap(int failedAttempts, int expectedSeconds) =>
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), OutboxProcessor.GetRetryDelay(failedAttempts, _options));
}
