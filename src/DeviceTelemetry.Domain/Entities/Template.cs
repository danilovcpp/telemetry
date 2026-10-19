using DeviceTelemetry.Domain.Common;

namespace DeviceTelemetry.Domain.Entities;

/// <summary>
/// Email notification template for a status code in one language. An active template for a status code is what makes
/// that status trigger a notification; at most one template exists per (status code, language).
/// </summary>
public class Template : BaseAuditableEntity
{
    public const int SubjectMaxLength = 200;
    public const int BodyMaxLength = 20_000;
    public const int LanguageMaxLength = 8;

    public int Id { get; set; }
    public int StatusCode { get; set; }

    /// <summary>
    /// Language code (<c>en</c>, <c>ru</c>, <c>de</c>). <c>null</c>: a language-neutral fallback, used when no template
    /// exists for the requested or the default language (templates created before languages existed are such).
    /// </summary>
    public string? Language { get; set; }

    public required string Subject { get; set; }
    public required string Body { get; set; }

    /// <summary>When <c>true</c> the body is HTML and substituted values are HTML-encoded.</summary>
    public bool IsHtml { get; set; }

    public bool IsActive { get; set; } = true;
}
