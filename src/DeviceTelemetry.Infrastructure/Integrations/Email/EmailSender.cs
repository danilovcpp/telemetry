using DeviceTelemetry.Application.Abstractions.Integrations;
using Identity.Infrastructure.Integrations.Email;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace DeviceTelemetry.Infrastructure.Integrations.Email;

public class EmailSender(
    IOptions<SmtpOptions> smtpOptions,
    ILogger<EmailSender> logger) : IEmailSender
{
    private readonly SmtpOptions _smtpOptions = smtpOptions.Value;

    public async Task SendEmailAsync(string to, string subject, string body, bool isHtml = true)
    {
        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(_smtpOptions.FromName, _smtpOptions.FromEmail));
            message.To.Add(MailboxAddress.Parse(to));
            message.Subject = subject;

            var bodyBuilder = new BodyBuilder();
            if (isHtml)
            {
                bodyBuilder.HtmlBody = body;
            }
            else
            {
                bodyBuilder.TextBody = body;
            }

            message.Body = bodyBuilder.ToMessageBody();

            using var client = new SmtpClient();

            // Connect to SMTP server
            await client.ConnectAsync(_smtpOptions.Host, _smtpOptions.Port,
                _smtpOptions.UseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls);

            // Authenticate if credentials are provided
            if (!string.IsNullOrEmpty(_smtpOptions.Username) && !string.IsNullOrEmpty(_smtpOptions.Password))
            {
                await client.AuthenticateAsync(_smtpOptions.Username, _smtpOptions.Password);
            }

            // Send email
            await client.SendAsync(message);
            await client.DisconnectAsync(true);

            logger.LogInformation("Email sent successfully to {To}", to);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send email to {To}", to);
            throw;
        }
    }
}
