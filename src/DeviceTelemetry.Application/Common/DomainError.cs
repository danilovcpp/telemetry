namespace DeviceTelemetry.Application.Common;

/// <summary>
/// Represents a predictable, expected failure within the domain.
/// Use this for business rule violations that callers must handle.
/// Do NOT use exceptions for these. Exceptions are reserved for programmer errors
/// and truly exceptional conditions.
/// </summary>
public sealed record DomainError
{
    public DomainError(
        string code,
        string message,
        DomainErrorType type = DomainErrorType.Validation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        Code = code;
        Message = message;
        Type = type;
    }

    /// <summary>
    /// Stable machine-readable code, e.g. "user.email.already_taken".
    /// Hierarchical, dot-separated, lowercase. This is part of public API contract.
    /// </summary>
    public string Code { get; }

    /// <summary>
    /// Human-readable description, English.
    /// Localization happens at the API boundary.
    /// </summary>
    public string Message { get; }

    public DomainErrorType Type { get; }

    public override string ToString()
        => $"{Code}: {Message}";
}

public enum DomainErrorType
{
    /// <summary>
    /// Input violates a domain rule. Maps to HTTP 400 / 422 typically.
    /// </summary>
    Validation,

    /// <summary>
    /// State transition is not allowed from the current state. Maps to HTTP 409.
    /// </summary>
    Conflict,

    /// <summary>
    /// Caller is not allowed to perform this action. Maps to HTTP 403.
    /// </summary>
    Forbidden,

    /// <summary>
    /// Required entity does not exist. Maps to HTTP 404.
    /// </summary>
    NotFound,
}