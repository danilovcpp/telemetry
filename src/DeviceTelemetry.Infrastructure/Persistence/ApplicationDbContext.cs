using System.Reflection;
using DeviceTelemetry.Application.Abstractions.Persistence;
using DeviceTelemetry.Domain;
using DeviceTelemetry.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace DeviceTelemetry.Infrastructure.Persistence;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
    : DbContext(options), IApplicationDbContext
{
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<Event> Events => Set<Event>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Template> Templates => Set<Template>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());
    }
    
    protected override void ConfigureConventions(ModelConfigurationBuilder b)
    {
        // Reasonable defaults for string columns.
        b.Properties<string>().HaveMaxLength(512);
    }
}