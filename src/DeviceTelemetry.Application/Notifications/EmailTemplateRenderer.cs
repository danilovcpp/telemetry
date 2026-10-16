using System.Globalization;
using System.Net;
using System.Text;
using DeviceTelemetry.Application.Common;
using DeviceTelemetry.Domain.Entities;

namespace DeviceTelemetry.Application.Notifications;

/// <summary>Values available to notification templates.</summary>
public sealed record NotificationContext(
    string DeviceCode,
    string DeviceName,
    string CustomerName,
    int StatusCode,
    Guid EventId,
    DateTime OccurredAtUtc,
    DateTime ReceivedAtUtc);

public sealed record RenderedEmail(string Subject, string Body);

/// <summary>
/// Renders a <see cref="Template"/> into an email subject and body.
/// <para>
/// Syntax: <c>{Name}</c> inserts a value from <see cref="SupportedPlaceholders"/>; <c>{{</c> and <c>}}</c> produce literal braces.
/// Rendering is strict: an unknown placeholder, an unterminated or stray brace, or a missing value makes the whole
/// render fail with a <c>template.invalid</c> error listing every problem. Nothing is silently replaced with an empty string.
/// </para>
/// <para>
/// Values are formatted with the invariant culture; timestamps as <c>yyyy-MM-dd HH:mm:ss UTC</c>.
/// In HTML bodies substituted values are HTML-encoded. The subject is plain text: control characters
/// (including line breaks) are replaced with spaces and it is truncated to <see cref="MaxSubjectLength"/> characters.
/// </para>
/// </summary>
public sealed class EmailTemplateRenderer
{
    public const int MaxSubjectLength = 255;

    public static IReadOnlyList<string> SupportedPlaceholders { get; } =
    [
        "DeviceId", "DeviceCode", "DeviceName", "CustomerName", "StatusCode", "EventId", "OccurredAtUtc", "ReceivedAtUtc",
    ];

    public Result<RenderedEmail> Render(Template template, NotificationContext context)
    {
        var problems = new List<string>();

        var subject = Substitute(template.Subject, context, encode: null, "subject", problems);
        var body = Substitute(template.Body, context, template.IsHtml ? WebUtility.HtmlEncode : null, "body", problems);

        if (problems.Count > 0)
        {
            return new DomainError(
                "template.invalid",
                $"Template {template.Id} for status {template.StatusCode} cannot be rendered: {string.Join("; ", problems)}.");
        }

        return new RenderedEmail(SanitizeSubject(subject), body);
    }

    private static string Substitute(
        string text, NotificationContext context, Func<string, string>? encode, string part, List<string> problems)
    {
        var result = new StringBuilder(text.Length + 64);

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (c == '{' && i + 1 < text.Length && text[i + 1] == '{')
            {
                result.Append('{');
                i++;
            }
            else if (c == '}' && i + 1 < text.Length && text[i + 1] == '}')
            {
                result.Append('}');
                i++;
            }
            else if (c == '}')
            {
                problems.Add($"stray '}}' in {part} at position {i}");
            }
            else if (c == '{')
            {
                var end = text.IndexOf('}', i + 1);
                if (end < 0)
                {
                    problems.Add($"unterminated placeholder in {part} at position {i}");
                    break;
                }

                var name = text.AsSpan(i + 1, end - i - 1);
                if (!TryGetValue(name, context, out var value))
                    problems.Add($"unknown placeholder '{{{name}}}' in {part}");
                else if (string.IsNullOrWhiteSpace(value))
                    problems.Add($"placeholder '{{{name}}}' in {part} has no value");
                else
                    result.Append(encode is null ? value : encode(value));

                i = end;
            }
            else
            {
                result.Append(c);
            }
        }

        return result.ToString();
    }

    private static bool TryGetValue(ReadOnlySpan<char> name, NotificationContext context, out string? value)
    {
        value = name switch
        {
            "DeviceId" or "DeviceCode" => context.DeviceCode,
            "DeviceName" => context.DeviceName,
            "CustomerName" => context.CustomerName,
            "StatusCode" => context.StatusCode.ToString(CultureInfo.InvariantCulture),
            "EventId" => context.EventId.ToString("D"),
            "OccurredAtUtc" => FormatTimestamp(context.OccurredAtUtc),
            "ReceivedAtUtc" => FormatTimestamp(context.ReceivedAtUtc),
            _ => null,
        };

        return value is not null || IsSupported(name);
    }

    private static bool IsSupported(ReadOnlySpan<char> name)
    {
        foreach (var supported in SupportedPlaceholders)
        {
            if (name.SequenceEqual(supported))
                return true;
        }

        return false;
    }

    private static string FormatTimestamp(DateTime utc) =>
        utc.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

    private static string SanitizeSubject(string subject)
    {
        var chars = subject.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (char.IsControl(chars[i]))
                chars[i] = ' ';
        }

        var sanitized = new string(chars).Trim();
        return sanitized.Length <= MaxSubjectLength ? sanitized : sanitized[..(MaxSubjectLength - 1)] + "…";
    }
}
