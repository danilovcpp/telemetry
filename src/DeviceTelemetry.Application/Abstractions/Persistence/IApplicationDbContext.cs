using DeviceTelemetry.Application.Outbox;
using DeviceTelemetry.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace DeviceTelemetry.Application.Abstractions.Persistence;

public interface IApplicationDbContext
{
    DbSet<Device> Devices { get; }
    DbSet<TelemetryEvent> TelemetryEvents { get; }
    DbSet<Customer> Customers { get; }
    DbSet<Template> Templates { get; }
    DbSet<OutboxMessage> OutboxMessages { get; }
    DbSet<DeviceLogFile> DeviceLogFiles { get; }

    /// <summary>
    /// Saves all pending changes in a single database transaction.
    /// </summary>
    /// <exception cref="Common.Exceptions.UniqueConstraintViolationException">A unique constraint was violated.</exception>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Starts an explicit transaction, for work that mixes set-based updates (<c>ExecuteUpdateAsync</c>, which run
    /// immediately) with <c>SaveChangesAsync</c> and must commit or roll back as one unit.
    /// </summary>
    Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken);
}
