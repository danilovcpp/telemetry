using System.Text.Json.Nodes;
using DeviceTelemetry.Application.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace DeviceTelemetry.Api.Http;

/// <summary>
/// Makes the OpenAPI document describe what actually goes over the wire, so generated clients get usable types:
/// <list type="bullet">
/// <item>properties written by <see cref="RawJsonConverter"/> are .NET strings but JSON objects on the wire (device payloads);</item>
/// <item><see cref="ProblemDetails"/> responses carry the extension members <c>code</c> and, for request validation, <c>errors</c>.</item>
/// </list>
/// </summary>
public sealed class RawJsonSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        if (context.JsonPropertyInfo?.CustomConverter is RawJsonConverter)
        {
            schema.Type = JsonSchemaType.Object | JsonSchemaType.Null;
            // Explicit free-form object: without it generators produce an "empty object" type.
            schema.AdditionalProperties = new OpenApiSchema();
            schema.Description = "Device-specific JSON object exactly as the device sent it; no fixed schema. Null when absent.";
        }

        if (context.JsonPropertyInfo is null && context.JsonTypeInfo.Type == typeof(ProblemDetails))
        {
            schema.Properties ??= new Dictionary<string, IOpenApiSchema>();
            schema.Properties["code"] = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Description = "Stable machine-readable error code, e.g. 'device.not_found'. Absent on request validation errors (see 'errors').",
                Examples = [JsonValue.Create("device.not_found")],
            };
            schema.Properties["errors"] = new OpenApiSchema
            {
                Type = JsonSchemaType.Object,
                Description = "Only on 400 request validation errors: invalid fields mapped to their messages.",
                AdditionalProperties = new OpenApiSchema
                {
                    Type = JsonSchemaType.Array,
                    Items = new OpenApiSchema { Type = JsonSchemaType.String },
                },
            };
            schema.Properties["traceId"] = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Description = "Request trace id; include it when reporting a problem.",
            };
        }

        return Task.CompletedTask;
    }
}
