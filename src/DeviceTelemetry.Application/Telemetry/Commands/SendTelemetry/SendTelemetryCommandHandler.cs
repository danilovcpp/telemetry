using System.Globalization;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using DeviceTelemetry.Application.Abstractions.Integrations;
using DeviceTelemetry.Application.Abstractions.Persistence;
using DeviceTelemetry.Application.Common;
using DeviceTelemetry.Application.Common.Exceptions;
using DeviceTelemetry.Application.Devices;
using DeviceTelemetry.Application.Notifications;
using DeviceTelemetry.Application.Outbox;
using DeviceTelemetry.Domain.Entities;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DeviceTelemetry.Application.Telemetry.Commands.SendTelemetry;

/// <summary>
/// Stores a telemetry event and, when an active template exists for its status code, queues an email notification
/// in the outbox. Both rows are written by one <c>SaveChanges</c> call, i.e. in one transaction. No email is sent here.
/// </summary>
public sealed class SendTelemetryCommandHandler(
    IApplicationDbContext context,
    IValidator<SendTelemetryCommand> validator,
    EmailTemplateRenderer templateRenderer,
    TimeProvider timeProvider,
    ILogger<SendTelemetryCommandHandler> logger)
    : ICommandHandler<SendTelemetryCommand, SendTelemetryResult>
{
    public async Task<Result<SendTelemetryResult>> HandleAsync(SendTelemetryCommand command, CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            return TelemetryErrors.Validation(string.Join(" ", validation.Errors.Select(e => e.ErrorMessage)));

        var device = await context.Devices
            .AsNoTracking()
            .Where(d => d.DeviceCode == command.DeviceId)
            .Select(d => new DeviceInfo(d.Id, d.DeviceCode, d.Name, d.ApiKeyHash, d.Customer.Name, d.Customer.Email))
            .SingleOrDefaultAsync(ct);

        if (device is null)
        {
            logger.LogWarning("Telemetry rejected: unknown device {DeviceCode}", command.DeviceId);
            return TelemetryErrors.DeviceAuthenticationFailed;
        }

        if (!DeviceApiKey.Verify(command.DeviceKey, device.ApiKeyHash))
        {
            logger.LogWarning(
                "Telemetry rejected: {Reason} for device {DeviceCode}",
                device.ApiKeyHash is null ? "no API key issued" : "invalid or missing API key",
                device.DeviceCode);
            return TelemetryErrors.DeviceAuthenticationFailed;
        }

        var contentHash = command.MessageId is null ? null : ComputeContentHash(command);

        if (command.MessageId is { } messageId)
        {
            var existing = await FindByMessageIdAsync(device.Id, messageId, ct);
            if (existing is not null)
                return ResolveDuplicate(existing, contentHash!, device);
        }

        var now = timeProvider.GetUtcNow();
        var receivedAtUtc = now.UtcDateTime;
        var telemetryEvent = new TelemetryEvent
        {
            Id = Guid.CreateVersion7(now),
            DeviceId = device.Id,
            StatusCode = command.StatusCode,
            OccurredAtUtc = command.OccurredAtUtc ?? receivedAtUtc,
            ReceivedAtUtc = receivedAtUtc,
            Payload = command.Payload,
            MessageId = command.MessageId,
            ContentHash = contentHash,
        };
        context.TelemetryEvents.Add(telemetryEvent);

        var notificationQueued = await TryQueueNotificationAsync(device, telemetryEvent, ct);

        try
        {
            await context.SaveChangesAsync(ct);
        }
        catch (UniqueConstraintViolationException) when (command.MessageId is { } racedMessageId)
        {
            // A concurrent request with the same messageId won the race; the unique index kept a single row.
            var existing = await FindByMessageIdAsync(device.Id, racedMessageId, ct);
            if (existing is null)
                throw;

            return ResolveDuplicate(existing, contentHash!, device);
        }

        logger.LogInformation(
            "Telemetry event {EventId} accepted from device {DeviceCode} with status {StatusCode}; notification queued: {NotificationQueued}",
            telemetryEvent.Id, device.DeviceCode, telemetryEvent.StatusCode, notificationQueued);

        return new SendTelemetryResult(telemetryEvent.Id, receivedAtUtc, IsDuplicate: false, notificationQueued);
    }

    /// <summary>
    /// Adds an outbox message when the status code has an active template. Every reason for not notifying is logged;
    /// none of them prevents the telemetry event itself from being stored.
    /// </summary>
    private async Task<bool> TryQueueNotificationAsync(DeviceInfo device, TelemetryEvent telemetryEvent, CancellationToken ct)
    {
        var template = await context.Templates
            .AsNoTracking()
            .SingleOrDefaultAsync(t => t.StatusCode == telemetryEvent.StatusCode && t.IsActive, ct);

        if (template is null)
            return false;

        if (string.IsNullOrWhiteSpace(device.CustomerEmail) || !MailAddress.TryCreate(device.CustomerEmail, out _))
        {
            logger.LogWarning(
                "Notification for event {EventId} (status {StatusCode}, device {DeviceCode}) skipped: customer has no valid email address",
                telemetryEvent.Id, telemetryEvent.StatusCode, device.DeviceCode);
            return false;
        }

        var rendered = templateRenderer.Render(
            template,
            new NotificationContext(
                device.DeviceCode,
                device.Name,
                device.CustomerName,
                telemetryEvent.StatusCode,
                telemetryEvent.Id,
                telemetryEvent.OccurredAtUtc,
                telemetryEvent.ReceivedAtUtc));

        if (rendered.IsFailure)
        {
            logger.LogError(
                "Notification for event {EventId} (status {StatusCode}, device {DeviceCode}) skipped: {ErrorCode} {ErrorMessage}",
                telemetryEvent.Id, telemetryEvent.StatusCode, device.DeviceCode, rendered.Error.Code, rendered.Error.Message);
            return false;
        }

        var email = new EmailMessage(device.CustomerEmail, rendered.Value.Subject, rendered.Value.Body, template.IsHtml);
        context.OutboxMessages.Add(OutboxMessage.ForEmail(
            Guid.CreateVersion7(timeProvider.GetUtcNow()), email, telemetryEvent.Id, telemetryEvent.ReceivedAtUtc));

        return true;
    }

    private Task<StoredEvent?> FindByMessageIdAsync(int deviceId, Guid messageId, CancellationToken ct) =>
        context.TelemetryEvents
            .AsNoTracking()
            .Where(e => e.DeviceId == deviceId && e.MessageId == messageId)
            .Select(e => new StoredEvent(e.Id, e.ReceivedAtUtc, e.ContentHash))
            .SingleOrDefaultAsync(ct);

    private Result<SendTelemetryResult> ResolveDuplicate(StoredEvent existing, byte[] contentHash, DeviceInfo device)
    {
        if (existing.ContentHash is null || !CryptographicOperations.FixedTimeEquals(existing.ContentHash, contentHash))
        {
            logger.LogWarning(
                "Telemetry rejected: messageId reused with different content for device {DeviceCode}; original event {EventId}",
                device.DeviceCode, existing.Id);
            return TelemetryErrors.MessageIdConflict;
        }

        logger.LogInformation(
            "Duplicate telemetry message from device {DeviceCode} ignored; original event {EventId}",
            device.DeviceCode, existing.Id);

        // NotificationQueued is false: no new notification was queued by this request.
        return new SendTelemetryResult(existing.Id, existing.ReceivedAtUtc, IsDuplicate: true, NotificationQueued: false);
    }

    /// <summary>
    /// Hash of what the device sent: status code, the occurredAtUtc it supplied (not the server default) and the payload.
    /// The payload is compared as normalized (minified) JSON text, so the same object with reordered keys counts as different.
    /// </summary>
    private static byte[] ComputeContentHash(SendTelemetryCommand command)
    {
        var canonical = string.Join(
            '\n',
            command.StatusCode.ToString(CultureInfo.InvariantCulture),
            command.OccurredAtUtc?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
            command.Payload ?? string.Empty);

        return SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
    }

    private sealed record DeviceInfo(
        int Id, string DeviceCode, string Name, byte[]? ApiKeyHash, string CustomerName, string? CustomerEmail);

    private sealed record StoredEvent(Guid Id, DateTime ReceivedAtUtc, byte[]? ContentHash);
}
