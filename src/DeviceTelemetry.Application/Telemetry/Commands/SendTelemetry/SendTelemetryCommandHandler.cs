using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeviceTelemetry.Application.Abstractions.Persistence;
using DeviceTelemetry.Application.Common;
using DeviceTelemetry.Application.Common.Exceptions;
using DeviceTelemetry.Application.Devices;
using DeviceTelemetry.Application.Notifications;
using DeviceTelemetry.Domain.Entities;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DeviceTelemetry.Application.Telemetry.Commands.SendTelemetry;

/// <summary>
/// Stores a telemetry event, records the device contact and the settings sent with it (<c>notifyEmail</c>,
/// <c>notifyLanguage</c>, <c>reportIntervalS</c>; see <see cref="DeviceContactRecorder"/>), and queues email notifications
/// in the outbox. Everything commits in one transaction; no email is sent here.
/// </summary>
public sealed class SendTelemetryCommandHandler(
    IApplicationDbContext context,
    IValidator<SendTelemetryCommand> validator,
    TelemetryNotifier notifier,
    DeviceContactRecorder contactRecorder,
    IOptions<TelemetryOptions> options,
    TimeProvider timeProvider,
    ILogger<SendTelemetryCommandHandler> logger)
    : ICommandHandler<SendTelemetryCommand, SendTelemetryResult>
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task<Result<SendTelemetryResult>> HandleAsync(SendTelemetryCommand command, CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            return TelemetryErrors.Validation(string.Join(" ", validation.Errors.Select(e => e.ErrorMessage)));

        var device = await context.Devices
            .AsNoTracking()
            .Where(d => d.DeviceCode == command.DeviceId)
            .Select(DeviceSnapshot.Projection)
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

        var channelsJson = command.Channels is null ? null : JsonSerializer.Serialize(command.Channels, SerializerOptions);
        var contentHash = command.MessageId is null ? null : ComputeContentHash(command, channelsJson);

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
            Source = TelemetryEventSource.Device,
            Channels = channelsJson,
        };

        // A device clock running ahead must not cost the event: keep it with the receive time and record the device's value.
        if (command.OccurredAtUtc > receivedAtUtc + options.Value.MaxClockSkew)
        {
            telemetryEvent.OccurredAtUtc = receivedAtUtc;
            telemetryEvent.RejectedOccurredAtUtc = command.OccurredAtUtc;
            logger.LogWarning(
                "Event {EventId} from device {DeviceCode}: occurredAtUtc {OccurredAtUtc:O} is in the future; using receive time",
                telemetryEvent.Id, device.DeviceCode, command.OccurredAtUtc);
        }

        // Settings sent along with telemetry; invalid values are ignored and kept on the event for diagnostics.
        var settings = DeviceSettingsResolver.Resolve(
            new DeviceSettingsInput(command.NotifyEmail, command.NotifyLanguage, command.ReportIntervalS));
        RecordRejected(settings, telemetryEvent, device);

        await using var transaction = await context.BeginTransactionAsync(ct);
        try
        {
            var updated = await contactRecorder.RecordAsync(device, settings, now, ct);

            context.TelemetryEvents.Add(telemetryEvent);
            var notificationQueued = await notifier.TryQueueAsync(updated.ToTarget(), telemetryEvent, command.Channels, ct);

            await context.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            logger.LogInformation(
                "Telemetry event {EventId} accepted from device {DeviceCode} with status {StatusCode}; notification queued: {NotificationQueued}",
                telemetryEvent.Id, device.DeviceCode, telemetryEvent.StatusCode, notificationQueued);

            return new SendTelemetryResult(telemetryEvent.Id, receivedAtUtc, IsDuplicate: false, notificationQueued);
        }
        catch (UniqueConstraintViolationException) when (command.MessageId is { } racedMessageId)
        {
            // A concurrent request with the same messageId won the race; the unique index kept a single row.
            // The failed transaction is unusable, so roll it back before reading the winner.
            await transaction.RollbackAsync(CancellationToken.None);

            var existing = await FindByMessageIdAsync(device.Id, racedMessageId, ct);
            if (existing is null)
                throw;

            return ResolveDuplicate(existing, contentHash!, device);
        }
    }

    private void RecordRejected(ResolvedDeviceSettings settings, TelemetryEvent telemetryEvent, DeviceSnapshot device)
    {
        if (settings.RejectedValues.TryGetValue("notifyEmail", out var email))
            telemetryEvent.RejectedNotifyEmail = email;
        if (settings.RejectedValues.TryGetValue("notifyLanguage", out var language))
            telemetryEvent.RejectedNotifyLanguage = language.Length <= 16 ? language : language[..16];
        if (settings.RejectedValues.TryGetValue("reportIntervalS", out var interval))
            telemetryEvent.RejectedReportIntervalS = int.Parse(interval, CultureInfo.InvariantCulture);

        foreach (var (field, reason) in settings.Rejected)
        {
            logger.LogWarning(
                "Event {EventId} from device {DeviceCode}: {Field} ignored ({Reason})",
                telemetryEvent.Id, device.DeviceCode, field, reason);
        }
    }

    private Task<StoredEvent?> FindByMessageIdAsync(int deviceId, Guid messageId, CancellationToken ct) =>
        context.TelemetryEvents
            .AsNoTracking()
            .Where(e => e.DeviceId == deviceId && e.MessageId == messageId)
            .Select(e => new StoredEvent(e.Id, e.ReceivedAtUtc, e.ContentHash))
            .SingleOrDefaultAsync(ct);

    private Result<SendTelemetryResult> ResolveDuplicate(StoredEvent existing, byte[] contentHash, DeviceSnapshot device)
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
    /// Hash of what the device sent: status code, the occurredAtUtc it supplied (not the server default, and as sent even
    /// when it was rejected as implausible), the payload, and — only when sent — the channel list.
    /// The payload and channels are compared as normalized (minified) JSON, so reordered keys or channels count as different.
    /// <para>
    /// Compatibility: the first three lines are the original formula. Fields added later contribute only when present,
    /// so hashes of messages from older firmware, and of events stored before the change, stay the same.
    /// Settings (<c>notifyEmail</c>, <c>notifyLanguage</c>, <c>reportIntervalS</c>) are not content and are never hashed.
    /// </para>
    /// </summary>
    private static byte[] ComputeContentHash(SendTelemetryCommand command, string? channelsJson)
    {
        var canonical = string.Join(
            '\n',
            command.StatusCode.ToString(CultureInfo.InvariantCulture),
            command.OccurredAtUtc?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
            command.Payload ?? string.Empty);

        if (channelsJson is not null)
            canonical += "\nchannels:" + channelsJson;

        return SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
    }

    private sealed record StoredEvent(Guid Id, DateTime ReceivedAtUtc, byte[]? ContentHash);
}
