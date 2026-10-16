using System.Text.Json;
using DeviceTelemetry.Application.Abstractions.Integrations;

namespace DeviceTelemetry.Application.Outbox;

/// <summary>
/// Transactional outbox record. It is written in the same transaction as the business change
/// and processed later by a background worker, so a crash between the two cannot lose the message.
/// </summary>
public sealed class OutboxMessage
{
    public const int TypeMaxLength = 100;
    public const int LockedByMaxLength = 200;
    public const int LastErrorMaxLength = 2000;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public Guid Id { get; set; }

    /// <summary>Message kind, see <see cref="OutboxMessageTypes"/>.</summary>
    public required string Type { get; set; }

    /// <summary>
    /// Serialized message content (<c>jsonb</c>). For emails this is the fully rendered message,
    /// so later template changes do not alter an already queued notification.
    /// </summary>
    public required string Content { get; set; }

    /// <summary>Telemetry event that produced this message; used for diagnostics.</summary>
    public Guid? TelemetryEventId { get; set; }

    public OutboxMessageStatus Status { get; set; }

    /// <summary>Number of delivery attempts started so far (incremented when a worker claims the message).</summary>
    public int Attempts { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime NextAttemptAtUtc { get; set; }

    /// <summary>Lease expiry while <see cref="OutboxMessageStatus.Processing"/>; another worker may reclaim it afterwards.</summary>
    public DateTime? LockedUntilUtc { get; set; }

    /// <summary>Identifier of the worker holding the lease.</summary>
    public string? LockedBy { get; set; }

    public DateTime? ProcessedAtUtc { get; set; }
    public string? LastError { get; set; }

    public static OutboxMessage ForEmail(Guid id, EmailMessage email, Guid? telemetryEventId, DateTime nowUtc) => new()
    {
        Id = id,
        Type = OutboxMessageTypes.Email,
        Content = JsonSerializer.Serialize(email, SerializerOptions),
        TelemetryEventId = telemetryEventId,
        Status = OutboxMessageStatus.Pending,
        CreatedAtUtc = nowUtc,
        NextAttemptAtUtc = nowUtc,
    };

    /// <exception cref="JsonException">Content is not a valid serialized email.</exception>
    public EmailMessage ReadEmail() =>
        JsonSerializer.Deserialize<EmailMessage>(Content, SerializerOptions)
        ?? throw new JsonException("Outbox content is empty.");
}

public enum OutboxMessageStatus
{
    /// <summary>Waiting for the first or next attempt (see <see cref="OutboxMessage.NextAttemptAtUtc"/>).</summary>
    Pending,

    /// <summary>Claimed by a worker until <see cref="OutboxMessage.LockedUntilUtc"/>.</summary>
    Processing,

    /// <summary>Delivered to the provider.</summary>
    Sent,

    /// <summary>Permanently failed or out of attempts; <see cref="OutboxMessage.LastError"/> explains why.</summary>
    Failed,
}

public static class OutboxMessageTypes
{
    public const string Email = "email";
}
