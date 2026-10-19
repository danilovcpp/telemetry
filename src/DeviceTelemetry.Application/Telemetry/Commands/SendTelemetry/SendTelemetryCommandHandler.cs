using System.Globalization;
using System.Net.Mail;
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
/// Stores a telemetry event, applies the device-reported settings (<c>notifyEmail</c>, <c>reportIntervalS</c>) and its
/// last-contact time, ends a silence episode (raising 190002), and queues email notifications in the outbox.
/// Everything commits in one transaction; no email is sent here.
/// </summary>
public sealed class SendTelemetryCommandHandler(
    IApplicationDbContext context,
    IValidator<SendTelemetryCommand> validator,
    TelemetryNotifier notifier,
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
            .Select(d => new DeviceInfo(d.Id, d.DeviceCode, d.Name, d.ApiKeyHash, d.NotifyEmail, d.Customer.Name, d.Customer.Email))
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

        var notifyEmail = ResolveNotifyEmail(command.NotifyEmail, telemetryEvent, device);
        var reportIntervalS = ResolveReportInterval(command.ReportIntervalS, telemetryEvent, device);

        await using var transaction = await context.BeginTransactionAsync(ct);
        try
        {
            // The first statement locks the device row until commit, so the silence monitor cannot mark the device silent
            // while this message is in flight, and two concurrent messages cannot both end the same silence episode.
            await context.Devices
                .Where(d => d.Id == device.Id)
                .ExecuteUpdateAsync(s =>
                {
                    s.SetProperty(d => d.LastSeenAtUtc, receivedAtUtc);
                    if (notifyEmail.Changed)
                        s.SetProperty(d => d.NotifyEmail, notifyEmail.Value);
                    if (reportIntervalS is not null)
                        s.SetProperty(d => d.ReportIntervalS, reportIntervalS);
                }, ct);

            var silentSinceUtc = await context.Devices
                .Where(d => d.Id == device.Id)
                .Select(d => d.SilentSinceUtc)
                .SingleAsync(ct);

            var target = new NotificationTarget(
                device.DeviceCode,
                device.Name,
                device.CustomerName,
                notifyEmail.Changed ? notifyEmail.Value : device.NotifyEmail,
                device.CustomerEmail);

            if (silentSinceUtc is not null)
            {
                await context.Devices
                    .Where(d => d.Id == device.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(d => d.SilentSinceUtc, (DateTime?)null), ct);

                var backOnline = ServerEvents.Create(
                    device.Id, TelemetryStatusCodes.DeviceBackOnline, now, new { silentSinceUtc });
                context.TelemetryEvents.Add(backOnline);
                await notifier.TryQueueAsync(target, backOnline, channels: null, ct);

                logger.LogInformation(
                    "Device {DeviceCode} reports again after silence since {SilentSinceUtc:O}; event {EventId} raised",
                    device.DeviceCode, silentSinceUtc, backOnline.Id);
            }

            context.TelemetryEvents.Add(telemetryEvent);
            var notificationQueued = await notifier.TryQueueAsync(target, telemetryEvent, command.Channels, ct);

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

    /// <summary>
    /// Decides what to do with a sent <c>notifyEmail</c>. Not sent: keep. Empty: clear. Valid: set (normalized).
    /// Invalid: keep the stored address, accept the event and record the rejected value on it.
    /// </summary>
    private NotifyEmailChange ResolveNotifyEmail(string? sent, TelemetryEvent telemetryEvent, DeviceInfo device)
    {
        if (sent is null)
            return NotifyEmailChange.None;

        var trimmed = sent.Trim();
        if (trimmed.Length == 0)
            return new NotifyEmailChange(true, null);

        if (trimmed.Length <= Device.NotifyEmailMaxLength
            && MailAddress.TryCreate(trimmed, out var address)
            && string.Equals(address.Address, trimmed, StringComparison.Ordinal)) // a bare address, no display name
        {
            return new NotifyEmailChange(true, address.Address);
        }

        telemetryEvent.RejectedNotifyEmail = trimmed.Length <= Device.NotifyEmailMaxLength ? trimmed : trimmed[..Device.NotifyEmailMaxLength];
        logger.LogWarning(
            "Event {EventId} from device {DeviceCode}: notifyEmail is not a valid address and was ignored",
            telemetryEvent.Id, device.DeviceCode);
        return NotifyEmailChange.None;
    }

    private int? ResolveReportInterval(int? sent, TelemetryEvent telemetryEvent, DeviceInfo device)
    {
        if (sent is null or (>= Device.MinReportIntervalS and <= Device.MaxReportIntervalS))
            return sent;

        telemetryEvent.RejectedReportIntervalS = sent;
        logger.LogWarning(
            "Event {EventId} from device {DeviceCode}: reportIntervalS {ReportIntervalS} is outside {Min}..{Max} and was ignored",
            telemetryEvent.Id, device.DeviceCode, sent, Device.MinReportIntervalS, Device.MaxReportIntervalS);
        return null;
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
    /// Hash of what the device sent: status code, the occurredAtUtc it supplied (not the server default, and as sent even
    /// when it was rejected as implausible), the payload, and — only when sent — the channel list.
    /// The payload and channels are compared as normalized (minified) JSON, so reordered keys or channels count as different.
    /// <para>
    /// Compatibility: the first three lines are the original formula. Fields added later contribute only when present,
    /// so hashes of messages from older firmware, and of events stored before the change, stay the same.
    /// <c>notifyEmail</c> and <c>reportIntervalS</c> are addressing/configuration, not content, and are never hashed.
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

    private sealed record DeviceInfo(
        int Id, string DeviceCode, string Name, byte[]? ApiKeyHash, string? NotifyEmail, string CustomerName, string? CustomerEmail);

    private sealed record StoredEvent(Guid Id, DateTime ReceivedAtUtc, byte[]? ContentHash);

    private readonly record struct NotifyEmailChange(bool Changed, string? Value)
    {
        public static readonly NotifyEmailChange None = new(false, null);
    }
}
