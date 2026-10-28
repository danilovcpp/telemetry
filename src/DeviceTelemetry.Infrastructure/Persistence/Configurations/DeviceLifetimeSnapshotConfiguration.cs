using DeviceTelemetry.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DeviceTelemetry.Infrastructure.Persistence.Configurations;

internal sealed class DeviceLifetimeSnapshotConfiguration : IEntityTypeConfiguration<DeviceLifetimeSnapshot>
{
    public void Configure(EntityTypeBuilder<DeviceLifetimeSnapshot> builder)
    {
        builder.ToTable("DeviceLifetimeSnapshots");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Lifetime).HasColumnType("jsonb").IsRequired().Metadata.SetMaxLength(null);

        builder.HasOne(s => s.Device)
            .WithMany(d => d.LifetimeSnapshots)
            .HasForeignKey(s => s.DeviceId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(s => s.EventId).IsUnique();

        // Mobile history newest first with keyset pagination, as for events.
        builder.HasIndex(s => new { s.DeviceId, s.OccurredAtUtc, s.EventId }).IsDescending(false, true, true);
    }
}
