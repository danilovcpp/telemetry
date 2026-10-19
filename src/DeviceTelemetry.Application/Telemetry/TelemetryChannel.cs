namespace DeviceTelemetry.Application.Telemetry;

/// <summary>A device channel an event refers to, e.g. <c>{"id": "ch2", "name": "130AA15P20"}</c>.</summary>
public sealed record TelemetryChannel(string Id, string? Name)
{
    public const int MaxCount = 32;
    public const int IdMaxLength = 16;
    public const int NameMaxLength = 32;

    /// <summary><c>ch2 (130AA15P20)</c>, or just <c>ch2</c> without a name.</summary>
    public override string ToString() => string.IsNullOrEmpty(Name) ? Id : $"{Id} ({Name})";
}
