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
/// <param name="DeviceLanguage">Language the device asked for (<c>Device.NotifyLanguage</c>); wins over the customer's.</param>
/// <param name="CustomerLanguage">The customer's language (<c>Customer.Language</c>).</param>
/// <param name="DeviceTimeZone">Device time zone for the "device time" line of emails.</param>
/// <param name="ChannelCatalog">Channel names the device reported; fill in names missing from an event's channel list.</param>
/// <param name="NotificationsDisabled">The device switched emails off: nothing is queued for this target.</param>
public sealed record NotificationTarget(
    string DeviceCode,
    string DeviceName,
    string CustomerName,
    string? DeviceNotifyEmail,
    string? CustomerEmail,
    string? DeviceLanguage = null,
    string? CustomerLanguage = null,
    string? DeviceTimeZone = null,
    IReadOnlyList<TelemetryChannel>? ChannelCatalog = null,
    bool NotificationsDisabled = false)
{
    public string Language => DeviceLanguage ?? CustomerLanguage ?? NotificationLanguages.Default;
}

/// <summary>
/// Queues the email notification for a telemetry event, device- or server-originated alike, when its status code has
/// an active template. Adds the outbox message to the context without saving, so it commits with the event.
/// Every reason for not notifying is logged; none of them is an error for the caller.
/// <para>
/// Template choice for the target's language: the template in that language, else in
/// <see cref="NotificationLanguages.Default"/>, else the language-neutral one (<c>Language = null</c>). The most specific
/// existing template decides, active or not: deactivating the Russian template of a code stops those emails instead of
/// silently sending the English one.
/// </para>
/// </summary>
public sealed class TelemetryNotifier(
    IApplicationDbContext context,
    EmailTemplateRenderer templateRenderer,
    TimeProvider timeProvider,
    ILogger<TelemetryNotifier> logger)
{
    /// <param name="notifyEmail">Value of the <c>{NotifyEmail}</c> placeholder (the new address, for 190003).</param>
    public async Task<bool> TryQueueAsync(
        NotificationTarget target,
        TelemetryEvent telemetryEvent,
        IReadOnlyList<TelemetryChannel>? channels,
        CancellationToken ct,
        string? notifyEmail = null)
    {
        var language = target.Language;
        var candidates = await context.Templates
            .AsNoTracking()
            .Where(t => t.StatusCode == telemetryEvent.StatusCode
                        && (t.Language == language || t.Language == NotificationLanguages.Default || t.Language == null))
            .ToListAsync(ct);

        var template = SelectTemplate(candidates, language);

        if (template is null || !template.IsActive)
        {
            telemetryEvent.Notification = NotificationOutcome.NoTemplate;
            return false;
        }

        if (template.Language != language)
        {
            logger.LogInformation(
                "Notification for event {EventId} (status {StatusCode}): no template in '{Language}', using '{TemplateLanguage}'",
                telemetryEvent.Id, telemetryEvent.StatusCode, language, template.Language ?? "neutral");
        }

        if (target.NotificationsDisabled)
        {
            telemetryEvent.Notification = NotificationOutcome.Disabled;
            logger.LogInformation(
                "Notification for event {EventId} (status {StatusCode}, device {DeviceCode}) skipped: emails switched off on the device",
                telemetryEvent.Id, telemetryEvent.StatusCode, target.DeviceCode);
            return false;
        }

        var recipient = target.DeviceNotifyEmail ?? target.CustomerEmail;
        if (string.IsNullOrWhiteSpace(recipient) || !MailAddress.TryCreate(recipient, out _))
        {
            telemetryEvent.Notification = NotificationOutcome.NoAddress;
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
                WithCatalogNames(channels, target.ChannelCatalog),
                target.DeviceTimeZone,
                notifyEmail));

        if (rendered.IsFailure)
        {
            telemetryEvent.Notification = NotificationOutcome.Failed;
            logger.LogError(
                "Notification for event {EventId} (status {StatusCode}, device {DeviceCode}) skipped: {ErrorCode} {ErrorMessage}",
                telemetryEvent.Id, telemetryEvent.StatusCode, target.DeviceCode, rendered.Error.Code, rendered.Error.Message);
            return false;
        }

        var email = new EmailMessage(recipient, rendered.Value.Subject, rendered.Value.Body, template.IsHtml);
        context.OutboxMessages.Add(OutboxMessage.ForEmail(
            Guid.CreateVersion7(timeProvider.GetUtcNow()), email, telemetryEvent.Id, telemetryEvent.ReceivedAtUtc));

        telemetryEvent.Notification = NotificationOutcome.Queued;
        telemetryEvent.NotificationLanguage = template.Language;
        // The subject is shown in the public mobile API: never store one that carries an address.
        var subject = rendered.Value.Subject;
        telemetryEvent.NotificationSubject = subject.Contains('@') ? null
            : subject.Length <= Template.SubjectMaxLength ? subject : subject[..Template.SubjectMaxLength];

        return true;
    }

    /// <summary>
    /// The template that decides for <paramref name="language"/>: in that language, else in
    /// <see cref="NotificationLanguages.Default"/>, else the neutral one. It decides even when inactive.
    /// </summary>
    public static Template? SelectTemplate(IEnumerable<Template> candidates, string language)
    {
        var list = candidates as IReadOnlyCollection<Template> ?? candidates.ToList();
        return list.FirstOrDefault(t => t.Language == language)
               ?? list.FirstOrDefault(t => t.Language == NotificationLanguages.Default)
               ?? list.FirstOrDefault(t => t.Language == null);
    }

    /// <summary>Channels named in the event win; ids without a name get the name from the device's catalog.</summary>
    private static IReadOnlyList<TelemetryChannel>? WithCatalogNames(
        IReadOnlyList<TelemetryChannel>? channels, IReadOnlyList<TelemetryChannel>? catalog)
    {
        if (channels is null || catalog is null)
            return channels;

        var names = catalog.Where(c => !string.IsNullOrEmpty(c.Name)).ToDictionary(c => c.Id, c => c.Name, StringComparer.Ordinal);
        return channels
            .Select(c => string.IsNullOrEmpty(c.Name) && names.TryGetValue(c.Id, out var name) ? c with { Name = name } : c)
            .ToList();
    }
}
