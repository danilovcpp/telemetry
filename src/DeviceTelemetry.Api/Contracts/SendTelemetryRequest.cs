using System.ComponentModel.DataAnnotations;
using System.Text.Json;

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

    /// <summary>The payload as compact JSON, or <c>null</c> when absent.</summary>
    public string? GetPayloadJson() =>
        Payload is { ValueKind: JsonValueKind.Object } payload ? JsonSerializer.Serialize(payload) : null;
}
