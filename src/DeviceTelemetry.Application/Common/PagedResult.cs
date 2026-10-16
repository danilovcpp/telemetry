namespace DeviceTelemetry.Application.Common;

public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    string? NextCursor,
    bool HasMore);