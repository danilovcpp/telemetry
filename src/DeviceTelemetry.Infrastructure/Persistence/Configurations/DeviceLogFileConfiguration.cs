using DeviceTelemetry.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DeviceTelemetry.Infrastructure.Persistence.Configurations;

internal sealed class DeviceLogFileConfiguration : IEntityTypeConfiguration<DeviceLogFile>
{
    public void Configure(EntityTypeBuilder<DeviceLogFile> builder)
    {
        builder.ToTable("DeviceLogFiles");
        builder.HasKey(f => f.Id);

        builder.Property(f => f.Month).HasMaxLength(7).IsRequired();
        builder.Property(f => f.Path).HasMaxLength(DeviceLogFile.PathMaxLength).IsRequired();
        builder.Ignore(f => f.IsComplete);

        builder.HasOne(f => f.Device)
            .WithMany(d => d.LogFiles)
            .HasForeignKey(f => f.DeviceId)
            .OnDelete(DeleteBehavior.Restrict);

        // A device path identifies the file; the sync upserts by it and /data/{path} looks it up by it.
        builder.HasIndex(f => new { f.DeviceId, f.Path }).IsUnique();

        // Month and day listings of one channel.
        builder.HasIndex(f => new { f.DeviceId, f.Channel, f.Month });
    }
}
