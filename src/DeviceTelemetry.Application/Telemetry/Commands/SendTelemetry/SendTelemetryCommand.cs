using DeviceTelemetry.Application.Common;

namespace DeviceTelemetry.Application.Telemetry.Commands.SendTelemetry;

/// <summary>
/// Accepts one telemetry message from a device. Deliberately a class rather than a record:
/// a record's generated <c>ToString</c> would expose <see cref="DeviceKey"/> if the command were ever logged.
/// </summary>
public sealed class SendTelemetryCommand : ICommand<SendTelemetryResult>
{
    /// <summary>External device identifier (<c>Device.DeviceCode</c>).</summary>
    public required string DeviceId { get; init; }

    /// <summary>Device API key presented by the caller. Secret.</summary>
    public string? DeviceKey { get; init; }

    public int StatusCode { get; init; }

    /// <summary>Optional device-generated message identifier that makes retries idempotent.</summary>
    public Guid? MessageId { get; init; }

    /// <summary>Optional device-side event time (UTC). Defaults to the server receive time.</summary>
    public DateTime? OccurredAtUtc { get; init; }

    /// <summary>Optional payload as a JSON object.</summary>
    public string? Payload { get; init; }

    /// <summary>
    /// Optional notification address for the device. <c>null</c> = not sent (stored value unchanged);
    /// empty or whitespace = clear the stored value; an invalid address is ignored and recorded on the event.
    /// Addressing, not content: not part of the idempotency hash.
    /// </summary>
    public string? NotifyEmail { get; init; }

    /// <summary>Optional channels the event refers to (e.g. for 100003/100004). Part of the hash only when sent.</summary>
    public IReadOnlyList<TelemetryChannel>? Channels { get; init; }

    /// <summary>
    /// Optional regular report period in seconds; enables silence detection for the device. Outside the allowed range
    /// it is ignored and recorded on the event. Not part of the idempotency hash.
    /// </summary>
    public int? ReportIntervalS { get; init; }
}

/// <param name="EventId">Identifier of the stored event (the original one for a duplicate).</param>
/// <param name="ReceivedAtUtc">When the server accepted the event.</param>
/// <param name="IsDuplicate">The message had already been accepted; nothing new was stored.</param>
/// <param name="NotificationQueued">An email notification was queued for delivery (not yet sent).</param>
public sealed record SendTelemetryResult(Guid EventId, DateTime ReceivedAtUtc, bool IsDuplicate, bool NotificationQueued);
