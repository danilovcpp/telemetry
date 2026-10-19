using System.Net.Mail;
using DeviceTelemetry.Application.Abstractions.Integrations;
using DeviceTelemetry.Application.Abstractions.Persistence;
using DeviceTelemetry.Application.Outbox;
using DeviceTelemetry.Application.Telemetry;
using DeviceTelemetry.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DeviceTelemetry.Application.Notifications;

/// <param name="DeviceNotifyEmail">Address the device asked for (<c>Device.NotifyEmail</c>); wins over the customer's email.</param>
/// <param name="CustomerEmail">The customer's address, used when the device has none.</param>
public sealed record NotificationTarget(
    string DeviceCode, string DeviceName, string CustomerName, string? DeviceNotifyEmail, string? CustomerEmail);

/// <summary>
/// Queues the email notification for a telemetry event, device- or server-originated alike, when its status code has
/// an active template. Adds the outbox message to the context without saving, so it commits with the event.
/// Every reason for not notifying is logged; none of them is an error for the caller.
/// </summary>
public sealed class TelemetryNotifier(
    IApplicationDbContext context,
    EmailTemplateRenderer templateRenderer,
    TimeProvider timeProvider,
    ILogger<TelemetryNotifier> logger)
{
    public async Task<bool> TryQueueAsync(
        NotificationTarget target, TelemetryEvent telemetryEvent, IReadOnlyList<TelemetryChannel>? channels, CancellationToken ct)
    {
        var template = await context.Templates
            .AsNoTracking()
            .SingleOrDefaultAsync(t => t.StatusCode == telemetryEvent.StatusCode && t.IsActive, ct);

        if (template is null)
            return false;

        var recipient = target.DeviceNotifyEmail ?? target.CustomerEmail;
        if (string.IsNullOrWhiteSpace(recipient) || !MailAddress.TryCreate(recipient, out _))
        {
            logger.LogWarning(
                "Notification for event {EventId} (status {StatusCode}, device {DeviceCode}) skipped: no valid recipient address",
                telemetryEvent.Id, telemetryEvent.StatusCode, target.DeviceCode);
            return false;
        }

        var rendered = templateRenderer.Render(
            template,
            new NotificationContext(
                target.DeviceCode,
                target.DeviceName,
                target.CustomerName,
                telemetryEvent.StatusCode,
                telemetryEvent.Id,
                telemetryEvent.OccurredAtUtc,
                telemetryEvent.ReceivedAtUtc,
                channels));

        if (rendered.IsFailure)
        {
            logger.LogError(
                "Notification for event {EventId} (status {StatusCode}, device {DeviceCode}) skipped: {ErrorCode} {ErrorMessage}",
                telemetryEvent.Id, telemetryEvent.StatusCode, target.DeviceCode, rendered.Error.Code, rendered.Error.Message);
            return false;
        }

        var email = new EmailMessage(recipient, rendered.Value.Subject, rendered.Value.Body, template.IsHtml);
        context.OutboxMessages.Add(OutboxMessage.ForEmail(
            Guid.CreateVersion7(timeProvider.GetUtcNow()), email, telemetryEvent.Id, telemetryEvent.ReceivedAtUtc));

        return true;
    }
}
