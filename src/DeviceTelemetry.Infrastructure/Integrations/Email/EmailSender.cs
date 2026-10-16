using System.Net.Sockets;
using DeviceTelemetry.Application.Abstractions.Integrations;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace DeviceTelemetry.Infrastructure.Integrations.Email;

/// <summary>
/// Sends email over SMTP with MailKit. Provider failures are translated into <see cref="EmailSendException"/>
/// with a transient/permanent classification; anything else propagates unchanged.
/// </summary>
public class EmailSender(IOptions<SmtpOptions> smtpOptions) : IEmailSender
{
    private readonly SmtpOptions _smtpOptions = smtpOptions.Value;

    public async Task SendAsync(EmailMessage email, string idempotencyKey, CancellationToken ct)
    {
        var message = BuildMessage(email, idempotencyKey);

        using var client = new SmtpClient();
        client.Timeout = (int)_smtpOptions.Timeout.TotalMilliseconds;

        try
        {
            await client.ConnectAsync(_smtpOptions.Host, _smtpOptions.Port, _smtpOptions.Security, ct);

            if (!string.IsNullOrEmpty(_smtpOptions.Username))
                await client.AuthenticateAsync(_smtpOptions.Username, _smtpOptions.Password, ct);

            await client.SendAsync(message, ct);
            await client.DisconnectAsync(quit: true, ct);
        }
        catch (SmtpCommandException ex)
        {
            // 4xx replies are temporary by definition (RFC 5321); 5xx are permanent rejections.
            var code = (int)ex.StatusCode;
            throw new EmailSendException(
                $"SMTP server replied {code} ({ex.ErrorCode}): {ex.Message}", isTransient: code is >= 400 and < 500, ex);
        }
        catch (AuthenticationException ex)
        {
            // Usually a configuration problem; retrying within the backoff window lets a credentials fix take effect.
            throw new EmailSendException("SMTP authentication failed.", isTransient: true, ex);
        }
        catch (Exception ex) when (ex is SmtpProtocolException or SslHandshakeException or IOException or SocketException or TimeoutException)
        {
            throw new EmailSendException($"SMTP connection failed: {ex.Message}", isTransient: true, ex);
        }
    }

    private MimeMessage BuildMessage(EmailMessage email, string idempotencyKey)
    {
        if (!MailboxAddress.TryParse(email.To, out var recipient))
            throw new EmailSendException("Recipient address is invalid.", isTransient: false);

        var message = new MimeMessage
        {
            Subject = email.Subject,
            // Stable Message-ID: a redelivered copy of the same outbox message carries the same id,
            // which lets receiving systems detect the duplicate.
            MessageId = $"{idempotencyKey}@{MailboxAddress.Parse(_smtpOptions.FromEmail).Domain}",
            Body = email.IsHtml
                ? new TextPart(MimeKit.Text.TextFormat.Html) { Text = email.Body }
                : new TextPart(MimeKit.Text.TextFormat.Plain) { Text = email.Body },
        };
        message.From.Add(new MailboxAddress(_smtpOptions.FromName, _smtpOptions.FromEmail));
        message.To.Add(recipient);

        return message;
    }
}
