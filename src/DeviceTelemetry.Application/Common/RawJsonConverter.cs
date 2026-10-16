using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeviceTelemetry.Application.Common;

/// <summary>
/// Writes a string that already holds JSON text (e.g. a <c>jsonb</c> column) as a JSON value instead of a
/// quoted string, without parsing it. Only use it for values that are guaranteed to be valid JSON.
/// </summary>
public sealed class RawJsonConverter : JsonConverter<string?>
{
    public override bool HandleNull => true;

    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        using var document = JsonDocument.ParseValue(ref reader);
        return document.RootElement.GetRawText();
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null)
            writer.WriteNullValue();
        else
            writer.WriteRawValue(value, skipInputValidation: true);
    }
}
