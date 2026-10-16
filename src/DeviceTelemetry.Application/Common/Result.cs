using DeviceTelemetry.Application.Common;

namespace Identity.Core;

/// <summary>
/// Result of a domain operation: either a success carrying a value, or a failure carrying a <see cref="DomainError"/>.
/// </summary>
public readonly record struct Result<T>
{
    private readonly T? _value;
    private readonly DomainError? _error;

    private Result(T value)
    {
        ArgumentNullException.ThrowIfNull(value);

        _value = value;
        _error = null;
        IsSuccess = true;
    }

    private Result(DomainError error)
    {
        _value = default;
        _error = error;
        IsSuccess = false;
    }

    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;

    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("Cannot access Value on a failed Result.");

    public DomainError Error => IsFailure
        ? _error!
        : throw new InvalidOperationException("Cannot access Error on a successful Result.");

    public static Result<T> Success(T value) => new(value);
    public static Result<T> Failure(DomainError error) => new(error);

    public static implicit operator Result<T>(T value) => Success(value);
    public static implicit operator Result<T>(DomainError error) => Failure(error);

    public TOut Match<TOut>(Func<T, TOut> onSuccess, Func<DomainError, TOut> onFailure) =>
        IsSuccess ? onSuccess(_value!) : onFailure(_error!);
}

/// <summary>
/// Non-generic Result for void operations.
/// </summary>
public readonly record struct Result
{
    private Result(bool isSuccess, DomainError? error)
    {
        IsSuccess = isSuccess;
        Error = error;
    }

    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;

    public DomainError Error => IsFailure
        ? field!
        : throw new InvalidOperationException("Cannot access Error on a successful Result.");

    public static Result Success() => new(true, null);
    public static Result Failure(DomainError error) => new(false, error);

    public static implicit operator Result(DomainError error) => Failure(error);
}
