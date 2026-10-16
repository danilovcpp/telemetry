namespace DeviceTelemetry.Application.Abstractions.Integrations;

/// <summary>A fully rendered email. Contains personal data: do not log it.</summary>
public sealed record EmailMessage(string To, string Subject, string Body, bool IsHtml);
