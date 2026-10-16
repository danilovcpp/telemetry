
namespace DeviceTelemetry.Application.Common;

/// <summary>
/// Marker for command messages. Commands mutate state and return a Result.
/// One handler per command; commands are processed synchronously in-process.
/// </summary>
public interface ICommand<TResponse>;

/// <summary>Command with no return payload.</summary>
public interface ICommand : ICommand<Unit>;

public readonly record struct Unit
{
    public static readonly Unit Value = default;
}

public interface ICommandHandler<in TCommand, TResponse>
    where TCommand : ICommand<TResponse>
{
    Task<Result<TResponse>> HandleAsync(TCommand command, CancellationToken ct);
}

public interface ICommandHandler<in TCommand> : ICommandHandler<TCommand, Unit>
    where TCommand : ICommand<Unit>;