namespace DeviceTelemetry.Application.Abstractions.Integrations;

public interface IEmailSender
{
    /// <summary>
    /// Delivers the message to the email provider.
    /// </summary>
    /// <param name="message">Fully rendered message.</param>
    /// <param name="idempotencyKey">
    /// Stable key for this logical message. It is passed on to the provider (for SMTP, as the Message-ID header)
    /// so repeated deliveries of the same message can be recognised as duplicates downstream.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="EmailSendException">The provider rejected the message or could not be reached.</exception>
    Task SendAsync(EmailMessage message, string idempotencyKey, CancellationToken ct);
}
