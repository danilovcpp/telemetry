using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using DeviceTelemetry.Application.Telemetry;

namespace DeviceTelemetry.Api.Contracts;

/// <summary>
/// Body of <c>POST /api/telemetry/send</c>. Only <see cref="DeviceId"/> and <see cref="StatusCode"/> are required;
/// the other fields are optional extensions of the original contract.
/// </summary>
public sealed class SendTelemetryRequest : IValidatableObject
{
    /// <summary>External device identifier (the device code), not the internal database id.</summary>
    [Required]
    public string? DeviceId { get; init; }

    [Required]
    public int? StatusCode { get; init; }

    /// <summary>Stable device-generated id; repeating it makes the request idempotent.</summary>
    public Guid? MessageId { get; init; }

    /// <summary>Device-side event time; must carry a UTC designator or an offset.</summary>
    public DateTime? OccurredAtUtc { get; init; }

    /// <summary>Optional JSON object with additional data.</summary>
    public JsonElement? Payload { get; init; }

    /// <summary>
    /// Optional notification address for this device. Absent or <c>null</c>: unchanged; empty: cleared.
    /// An invalid address does not fail the request: it is ignored and recorded on the event.
    /// </summary>
    public string? NotifyEmail { get; init; }

    /// <summary>Optional channels the event refers to, at most 32 (<c>id</c> ≤ 16 characters, <c>name</c> ≤ 32).</summary>
    public IReadOnlyList<ChannelRequest?>? Channels { get; init; }

    /// <summary>
    /// Optional regular report period, 10–86400 seconds; enables "device silent" detection (190001).
    /// Out of range: ignored and recorded on the event, the request still succeeds.
    /// </summary>
    public int? ReportIntervalS { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (OccurredAtUtc is { Kind: DateTimeKind.Unspecified })
        {
            yield return new ValidationResult(
                "'occurredAtUtc' must include a time zone, e.g. '2026-10-16T10:30:00Z'.", [nameof(OccurredAtUtc)]);
        }

        if (Payload is { ValueKind: not (JsonValueKind.Object or JsonValueKind.Null) })
            yield return new ValidationResult("'payload' must be a JSON object.", [nameof(Payload)]);
    }

    /// <summary>System.Text.Json converts timestamps with an offset to local time; convert them back to UTC.</summary>
    public DateTime? GetOccurredAtUtc() => OccurredAtUtc?.Kind switch
    {
        DateTimeKind.Local => OccurredAtUtc.Value.ToUniversalTime(),
        _ => OccurredAtUtc,
    };

    /// <summary>
    /// Channels for the command; a <c>null</c> item stays <c>null</c> and a missing id becomes empty,
    /// so the command validator reports them with the other rules.
    /// </summary>
    public IReadOnlyList<TelemetryChannel>? GetChannels() =>
        Channels?.Select(c => c is null ? null! : new TelemetryChannel(c.Id ?? string.Empty, c.Name)).ToList();

    /// <summary>The payload as compact JSON, or <c>null</c> when absent.</summary>
    public string? GetPayloadJson() =>
        Payload is { ValueKind: JsonValueKind.Object } payload ? JsonSerializer.Serialize(payload) : null;
}

/// <param name="Id">Channel id, e.g. <c>ch2</c>.</param>
/// <param name="Name">Optional channel name, e.g. <c>130AA15P20</c>.</param>
public sealed record ChannelRequest(string? Id, string? Name);
