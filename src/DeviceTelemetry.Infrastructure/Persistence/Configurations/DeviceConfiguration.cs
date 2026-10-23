using System.Security.Cryptography;
using DeviceTelemetry.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DeviceTelemetry.Infrastructure.Persistence.Configurations;

internal sealed class DeviceConfiguration : IEntityTypeConfiguration<Device>
{
    public void Configure(EntityTypeBuilder<Device> builder)
    {
        builder.ToTable("Devices", t => t.HasCheckConstraint(
            "CK_Devices_ApiKeyHash_Length",
            $"\"ApiKeyHash\" IS NULL OR octet_length(\"ApiKeyHash\") = {SHA256.HashSizeInBytes}"));

        builder.HasKey(d => d.Id);

        builder.Property(d => d.Name).HasMaxLength(Device.NameMaxLength).IsRequired();
        builder.Property(d => d.DeviceCode).HasMaxLength(Device.DeviceCodeMaxLength).IsRequired();

        builder.Property(d => d.NotifyEmail).HasMaxLength(Device.NotifyEmailMaxLength);
        builder.Property(d => d.NotifyLanguage).HasMaxLength(Template.LanguageMaxLength);
        builder.Property(d => d.TimeZone).HasMaxLength(Device.TimeZoneMaxLength);
        builder.Property(d => d.Channels).HasColumnType("jsonb").Metadata.SetMaxLength(null);
        builder.Property(d => d.LinkHello).HasColumnType("jsonb").Metadata.SetMaxLength(null);

        builder.HasIndex(d => d.DeviceCode).IsUnique();

        builder.HasOne(d => d.Customer)
            .WithMany(c => c.Devices)
            .HasForeignKey(d => d.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
