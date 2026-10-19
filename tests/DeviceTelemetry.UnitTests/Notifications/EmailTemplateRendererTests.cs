using System.Globalization;
using DeviceTelemetry.Application.Notifications;
using DeviceTelemetry.Domain.Entities;

namespace DeviceTelemetry.UnitTests.Notifications;

public sealed class EmailTemplateRendererTests
{
    private static readonly Guid EventId = Guid.Parse("0199ee2c-5c1e-7000-8000-000000000001");

    private static readonly NotificationContext Context = new(
        DeviceCode: "uid-42",
        DeviceName: "Boiler #1",
        CustomerName: "ACME Corp",
        StatusCode: 100001,
        EventId: EventId,
        OccurredAtUtc: new DateTime(2026, 10, 16, 10, 30, 0, DateTimeKind.Utc),
        ReceivedAtUtc: new DateTime(2026, 10, 16, 10, 30, 5, DateTimeKind.Utc));

    private readonly EmailTemplateRenderer _renderer = new();

    private static Template Template(string subject = "Alert", string body = "Body", bool isHtml = false) =>
        new() { Id = 7, StatusCode = 100001, Subject = subject, Body = body, IsHtml = isHtml };

    [Fact]
    public void Substitutes_every_supported_placeholder()
    {
        var body = string.Join("|", EmailTemplateRenderer.SupportedPlaceholders.Select(p => $"{{{p}}}"));

        var result = _renderer.Render(Template(body: body), Context with { Channels = [new("ch2", "130AA15P20")] });

        Assert.True(result.IsSuccess);
        Assert.Equal(
            $"uid-42|uid-42|Boiler #1|ACME Corp|100001|{EventId}|2026-10-16 10:30:00 UTC|2026-10-16 10:30:05 UTC|ch2 (130AA15P20)",
            result.Value.Body);
    }

    [Fact]
    public void Channels_render_as_id_and_name_list()
    {
        var context = Context with { Channels = [new("ch2", "130AA15P20"), new("ch5", null), new("ch7", "")] };

        var result = _renderer.Render(Template(subject: "Каналы: {Channels}", body: "{Channels}"), context);

        Assert.Equal("Каналы: ch2 (130AA15P20), ch5, ch7", result.Value.Subject);
        Assert.Equal("ch2 (130AA15P20), ch5, ch7", result.Value.Body);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Missing_channels_render_empty_instead_of_failing(bool emptyList)
    {
        var context = Context with { Channels = emptyList ? [] : null };

        var result = _renderer.Render(Template(subject: "Не отвечают каналы: {Channels}", body: "[{Channels}]"), context);

        Assert.True(result.IsSuccess);
        Assert.Equal("Не отвечают каналы:", result.Value.Subject);
        Assert.Equal("[]", result.Value.Body);
    }

    [Fact]
    public void Channel_names_are_html_encoded_in_html_bodies()
    {
        var context = Context with { Channels = [new("ch1", "<b>x</b>")] };

        var result = _renderer.Render(Template(body: "<p>{Channels}</p>", isHtml: true), context);

        Assert.Equal("<p>ch1 (&lt;b&gt;x&lt;/b&gt;)</p>", result.Value.Body);
    }

    [Fact]
    public void Old_double_brace_syntax_is_a_literal_not_a_placeholder() =>
        Assert.Equal("{channels}", _renderer.Render(Template(body: "{{channels}}"), Context).Value.Body);

    [Fact]
    public void Formatting_does_not_depend_on_current_culture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ar-SA");

            var result = _renderer.Render(Template(body: "{StatusCode} {OccurredAtUtc}"), Context with { StatusCode = 1234567 });

            Assert.Equal("1234567 2026-10-16 10:30:00 UTC", result.Value.Body);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Unknown_placeholder_fails_instead_of_rendering_empty_text()
    {
        var result = _renderer.Render(Template(body: "Battery {BatteryLevel}, user {customerName}"), Context);

        Assert.True(result.IsFailure);
        Assert.Equal("template.invalid", result.Error.Code);
        Assert.Contains("{BatteryLevel}", result.Error.Message);
        Assert.Contains("{customerName}", result.Error.Message); // placeholder names are case-sensitive
    }

    [Fact]
    public void Missing_value_fails()
    {
        var result = _renderer.Render(Template(body: "Hello {CustomerName}"), Context with { CustomerName = " " });

        Assert.True(result.IsFailure);
        Assert.Contains("{CustomerName}", result.Error.Message);
        Assert.Contains("has no value", result.Error.Message);
    }

    [Theory]
    [InlineData("Status {StatusCode", "unterminated")]
    [InlineData("Status } here", "stray")]
    public void Malformed_braces_fail(string body, string expectedProblem)
    {
        var result = _renderer.Render(Template(body: body), Context);

        Assert.True(result.IsFailure);
        Assert.Contains(expectedProblem, result.Error.Message);
    }

    [Fact]
    public void Doubled_braces_produce_literal_braces()
    {
        var result = _renderer.Render(Template(body: "{{StatusCode}} = {StatusCode}; p {{ color: red }}"), Context);

        Assert.Equal("{StatusCode} = 100001; p { color: red }", result.Value.Body);
    }

    [Fact]
    public void Html_body_encodes_substituted_values_but_not_the_template_markup()
    {
        var context = Context with { DeviceName = "<script>alert('x')</script> & \"co\"" };

        var result = _renderer.Render(Template(body: "<p>Device <b>{DeviceName}</b></p>", isHtml: true), context);

        Assert.Equal(
            "<p>Device <b>&lt;script&gt;alert(&#39;x&#39;)&lt;/script&gt; &amp; &quot;co&quot;</b></p>",
            result.Value.Body);
    }

    [Fact]
    public void Plain_text_body_is_not_html_encoded()
    {
        var result = _renderer.Render(Template(body: "Device {DeviceName}"), Context with { DeviceName = "A & B <x>" });

        Assert.Equal("Device A & B <x>", result.Value.Body);
    }

    [Fact]
    public void Subject_cannot_be_split_into_extra_header_lines()
    {
        var context = Context with { DeviceName = "Boiler\r\nBcc: attacker@example.com" };

        var result = _renderer.Render(Template(subject: "Alert for {DeviceName}"), context);

        Assert.DoesNotContain('\r', result.Value.Subject);
        Assert.DoesNotContain('\n', result.Value.Subject);
        Assert.Equal("Alert for Boiler  Bcc: attacker@example.com", result.Value.Subject);
    }

    [Fact]
    public void Subject_is_not_html_encoded_and_is_truncated()
    {
        var context = Context with { DeviceName = "A & B " + new string('x', 400) };

        var result = _renderer.Render(Template(subject: "{DeviceName}", isHtml: true), context);

        Assert.StartsWith("A & B x", result.Value.Subject);
        Assert.Equal(EmailTemplateRenderer.MaxSubjectLength, result.Value.Subject.Length);
        Assert.EndsWith("…", result.Value.Subject);
    }
}
