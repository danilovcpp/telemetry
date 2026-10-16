using DeviceTelemetry.Application.Outbox;
using DeviceTelemetry.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace DeviceTelemetry.Application.Abstractions.Persistence;

public interface IApplicationDbContext
{
    DbSet<Device> Devices { get; }
    DbSet<TelemetryEvent> TelemetryEvents { get; }
    DbSet<Customer> Customers { get; }
    DbSet<Template> Templates { get; }
    DbSet<OutboxMessage> OutboxMessages { get; }

    /// <summary>
    /// Saves all pending changes in a single database transaction.
    /// </summary>
    /// <exception cref="Common.Exceptions.UniqueConstraintViolationException">A unique constraint was violated.</exception>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}
