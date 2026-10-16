using DeviceTelemetry.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DeviceTelemetry.Infrastructure.Persistence.Configurations;

internal sealed class TelemetryEventConfiguration : IEntityTypeConfiguration<TelemetryEvent>
{
    public void Configure(EntityTypeBuilder<TelemetryEvent> builder)
    {
        builder.ToTable("TelemetryEvents");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();

        builder.Property(e => e.Payload).HasColumnType("jsonb").Metadata.SetMaxLength(null); // size is limited by validation, not the column

        builder.HasOne(e => e.Device)
            .WithMany(d => d.Events)
            .HasForeignKey(e => e.DeviceId)
            .OnDelete(DeleteBehavior.Restrict);

        // Device history ordered by time (newest first); also serves as the FK index.
        builder.HasIndex(e => new { e.DeviceId, e.OccurredAtUtc }).IsDescending(false, true);

        // Events of a given status across devices, e.g. "all errors in the last hour".
        builder.HasIndex(e => new { e.StatusCode, e.OccurredAtUtc }).IsDescending(false, true);

        // Idempotency invariant: one event per device-supplied message id.
        builder.HasIndex(e => new { e.DeviceId, e.MessageId })
            .IsUnique()
            .HasFilter("\"MessageId\" IS NOT NULL");
    }
}
