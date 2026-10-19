using System.Text;
using System.Text.Json;
using DeviceTelemetry.Domain.Entities;
using FluentValidation;
using Microsoft.Extensions.Options;

namespace DeviceTelemetry.Application.Telemetry.Commands.SendTelemetry;

/// <summary>
/// Rejects messages whose <em>content</em> is invalid. Metadata problems (an implausible future <c>occurredAtUtc</c>,
/// an invalid <c>notifyEmail</c> or <c>reportIntervalS</c>) are deliberately not validation errors: a 400 is final for a
/// device, and losing an alarm is worse than a bad timestamp or address. The handler accepts such messages and records
/// the rejected values on the event.
/// </summary>
public sealed class SendTelemetryCommandValidator : AbstractValidator<SendTelemetryCommand>
{
    /// <summary>Earlier timestamps almost always come from a device whose clock was never set.</summary>
    private static readonly DateTime MinOccurredAtUtc = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public SendTelemetryCommandValidator(IOptions<TelemetryOptions> options)
    {
        var settings = options.Value;

        RuleFor(c => c.DeviceId)
            .NotEmpty()
            .MaximumLength(Device.DeviceCodeMaxLength);

        RuleFor(c => c.StatusCode)
            .GreaterThanOrEqualTo(0)
            .Must(code => !TelemetryStatusCodes.IsReservedForServer(code))
            .WithMessage("'statusCode' 190000–199999 is reserved for events raised by the server.");

        RuleFor(c => c.MessageId)
            .NotEqual(Guid.Empty)
            .When(c => c.MessageId.HasValue);

        RuleFor(c => c.OccurredAtUtc!.Value)
            .Must(t => t.Kind == DateTimeKind.Utc).WithMessage("'occurredAtUtc' must be a UTC timestamp.")
            .GreaterThanOrEqualTo(MinOccurredAtUtc)
            .OverridePropertyName("occurredAtUtc")
            .When(c => c.OccurredAtUtc.HasValue);

        RuleFor(c => c.Payload!)
            .Must(p => Encoding.UTF8.GetByteCount(p) <= settings.MaxPayloadBytes)
            .WithMessage($"'payload' must not exceed {settings.MaxPayloadBytes} bytes.")
            .Must(IsJsonObject)
            .WithMessage("'payload' must be a JSON object.")
            .OverridePropertyName("payload")
            .When(c => c.Payload is not null);

        // Channels are event content (they are hashed), so a malformed list is rejected rather than silently altered.
        RuleFor(c => c.Channels!)
            .Must(list => list.Count <= TelemetryChannel.MaxCount)
            .WithMessage($"'channels' must not contain more than {TelemetryChannel.MaxCount} items.")
            .OverridePropertyName("channels")
            .When(c => c.Channels is not null);

        RuleForEach(c => c.Channels)
            .NotNull().WithMessage("'channels' items must be objects.")
            .ChildRules(channel =>
            {
                channel.RuleFor(ch => ch.Id)
                    .NotEmpty().WithMessage("'channels[].id' is required.")
                    .MaximumLength(TelemetryChannel.IdMaxLength)
                    .WithMessage($"'channels[].id' must not exceed {TelemetryChannel.IdMaxLength} characters.");
                channel.RuleFor(ch => ch.Name)
                    .MaximumLength(TelemetryChannel.NameMaxLength)
                    .WithMessage($"'channels[].name' must not exceed {TelemetryChannel.NameMaxLength} characters.");
            })
            .OverridePropertyName("channels")
            .When(c => c.Channels is not null);
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
