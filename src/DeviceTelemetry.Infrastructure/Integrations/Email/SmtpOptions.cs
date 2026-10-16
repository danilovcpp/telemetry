using System.ComponentModel.DataAnnotations;
using MailKit.Security;

namespace DeviceTelemetry.Infrastructure.Integrations.Email;

/// <summary>
/// SMTP settings (section <c>Smtp</c>). Keep <see cref="Password"/> out of appsettings: use user secrets locally
/// and environment variables (<c>Smtp__Password</c>) or a secret store in production.
/// </summary>
public class SmtpOptions : IValidatableObject
{
    public const string SectionName = "Smtp";

    [Required]
    public string Host { get; set; } = string.Empty;

    [Range(1, 65535)]
    public int Port { get; set; } = 587;

    /// <summary><c>StartTls</c> (port 587), <c>SslOnConnect</c> (port 465), or <c>None</c> for a local test server only.</summary>
    public SecureSocketOptions Security { get; set; } = SecureSocketOptions.StartTls;

    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    [Required, EmailAddress]
    public string FromEmail { get; set; } = string.Empty;

    public string FromName { get; set; } = string.Empty;

    [Range(typeof(TimeSpan), "00:00:01", "00:05:00")]
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!string.IsNullOrEmpty(Username) && string.IsNullOrEmpty(Password))
            yield return new ValidationResult("Smtp:Password is required when Smtp:Username is set.", [nameof(Password)]);
    }
}
