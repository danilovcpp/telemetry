namespace DeviceTelemetry.Api.Contracts;

/// <param name="EventId">Identifier of the stored event.</param>
/// <param name="ReceivedAtUtc">When the server accepted the event.</param>
/// <param name="Duplicate">The message had already been accepted earlier (same messageId); nothing new was stored.</param>
/// <param name="NotificationQueued">
/// This request queued an email notification. Queued does not mean sent: delivery happens asynchronously.
/// </param>
public sealed record TelemetryAcceptedResponse(Guid EventId, DateTime ReceivedAtUtc, bool Duplicate, bool NotificationQueued);
