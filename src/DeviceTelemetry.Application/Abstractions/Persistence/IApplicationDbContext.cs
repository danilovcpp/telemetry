using DeviceTelemetry.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace DeviceTelemetry.Application.Abstractions.Persistence;

public interface IApplicationDbContext
{
    DbSet<Device> Devices { get; }
    DbSet<Event> Events { get; }
    DbSet<Customer> Customers { get; }
    DbSet<Template> Templates { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken);

}