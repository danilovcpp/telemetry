
namespace DeviceTelemetry.Application.Common;

public interface IQuery<TResponse> { }

public interface IQueryHandler<in TQuery, TResponse>
    where TQuery : IQuery<TResponse>
{
    Task<Result<TResponse>> HandleAsync(TQuery query, CancellationToken ct);
}