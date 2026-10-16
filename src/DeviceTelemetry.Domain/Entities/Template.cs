using DeviceTelemetry.Domain.Common;

namespace DeviceTelemetry.Domain.Entities;

/// <summary>
/// Email notification template for a status code. An active template for a status code is what makes
/// that status trigger a notification; at most one template exists per status code.
/// </summary>
public class Template : BaseAuditableEntity
{
    public const int SubjectMaxLength = 200;
    public const int BodyMaxLength = 20_000;

    public int Id { get; set; }
    public int StatusCode { get; set; }
    public required string Subject { get; set; }
    public required string Body { get; set; }

    /// <summary>When <c>true</c> the body is HTML and substituted values are HTML-encoded.</summary>
    public bool IsHtml { get; set; }

    public bool IsActive { get; set; } = true;
}
