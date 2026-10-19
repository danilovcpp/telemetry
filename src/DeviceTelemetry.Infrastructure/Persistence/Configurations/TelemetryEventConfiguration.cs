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
        builder.Property(e => e.Channels).HasColumnType("jsonb").Metadata.SetMaxLength(null);
        builder.Property(e => e.Source).HasConversion<string>().HasMaxLength(10);
        builder.Property(e => e.RejectedNotifyEmail).HasMaxLength(Device.NotifyEmailMaxLength);

        builder.HasOne(e => e.Device)
            .WithMany(d => d.Events)
            .HasForeignKey(e => e.DeviceId)
            .OnDelete(DeleteBehavior.Restrict);

        // Device history newest first, with Id as tie-breaker: matches the mobile history ORDER BY and keyset cursor
        // exactly, serves the "last event" LIMIT 1 lookup, and doubles as the FK index.
        builder.HasIndex(e => new { e.DeviceId, e.OccurredAtUtc, e.Id }).IsDescending(false, true, true);

        // Latest event of one status for one device, e.g. the last regular report (100010) for the device state screen.
        builder.HasIndex(e => new { e.DeviceId, e.StatusCode, e.OccurredAtUtc, e.Id }).IsDescending(false, false, true, true);

        // Events of a given status across devices, e.g. "all errors in the last hour".
        builder.HasIndex(e => new { e.StatusCode, e.OccurredAtUtc }).IsDescending(false, true);

        // Idempotency invariant: one event per device-supplied message id.
        builder.HasIndex(e => new { e.DeviceId, e.MessageId })
            .IsUnique()
            .HasFilter("\"MessageId\" IS NOT NULL");
    }
}
