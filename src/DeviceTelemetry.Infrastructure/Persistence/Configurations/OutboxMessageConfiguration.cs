using DeviceTelemetry.Application.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DeviceTelemetry.Infrastructure.Persistence.Configurations;

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("OutboxMessages");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();

        builder.Property(m => m.Type).HasMaxLength(OutboxMessage.TypeMaxLength).IsRequired();
        builder.Property(m => m.Content).HasColumnType("jsonb").IsRequired().Metadata.SetMaxLength(null);
        builder.Property(m => m.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(m => m.LockedBy).HasMaxLength(OutboxMessage.LockedByMaxLength);
        builder.Property(m => m.LastError).HasMaxLength(OutboxMessage.LastErrorMaxLength);

        // Supports the worker's claim query; sent and failed rows are excluded so the index stays small.
        builder.HasIndex(m => m.NextAttemptAtUtc)
            .HasDatabaseName("IX_OutboxMessages_Due")
            .HasFilter("\"Status\" IN ('Pending', 'Processing')");
    }
}
