namespace DeviceTelemetry.Application.Common.Exceptions;

/// <summary>
/// Thrown by the persistence layer when saving violates a unique constraint,
/// so that application code can react without depending on a database provider.
/// </summary>
public sealed class UniqueConstraintViolationException(string? constraintName, Exception innerException)
    : Exception($"Unique constraint '{constraintName}' was violated.", innerException)
{
    public string? ConstraintName { get; } = constraintName;
}
