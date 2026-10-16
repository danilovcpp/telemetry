using System.Text;
using System.Text.Json;
using DeviceTelemetry.Domain.Entities;
using FluentValidation;
using Microsoft.Extensions.Options;

namespace DeviceTelemetry.Application.Telemetry.Commands.SendTelemetry;

public sealed class SendTelemetryCommandValidator : AbstractValidator<SendTelemetryCommand>
{
    /// <summary>Earlier timestamps almost always come from a device whose clock was never set.</summary>
    private static readonly DateTime MinOccurredAtUtc = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public SendTelemetryCommandValidator(IOptions<TelemetryOptions> options, TimeProvider timeProvider)
    {
        var settings = options.Value;

        RuleFor(c => c.DeviceId)
            .NotEmpty()
            .MaximumLength(Device.DeviceCodeMaxLength);

        RuleFor(c => c.StatusCode)
            .GreaterThanOrEqualTo(0);

        RuleFor(c => c.MessageId)
            .NotEqual(Guid.Empty)
            .When(c => c.MessageId.HasValue);

        RuleFor(c => c.OccurredAtUtc!.Value)
            .Must(t => t.Kind == DateTimeKind.Utc).WithMessage("'occurredAtUtc' must be a UTC timestamp.")
            .GreaterThanOrEqualTo(MinOccurredAtUtc)
            .Must(t => t <= timeProvider.GetUtcNow().UtcDateTime + settings.MaxClockSkew)
            .WithMessage("'occurredAtUtc' must not be in the future.")
            .OverridePropertyName("occurredAtUtc")
            .When(c => c.OccurredAtUtc.HasValue);

        RuleFor(c => c.Payload!)
            .Must(p => Encoding.UTF8.GetByteCount(p) <= settings.MaxPayloadBytes)
            .WithMessage($"'payload' must not exceed {settings.MaxPayloadBytes} bytes.")
            .Must(IsJsonObject)
            .WithMessage("'payload' must be a JSON object.")
            .OverridePropertyName("payload")
            .When(c => c.Payload is not null);
    }

    private static bool IsJsonObject(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
