using DeviceTelemetry.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DeviceTelemetry.Infrastructure.Persistence.Configurations;

internal sealed class TemplateConfiguration : IEntityTypeConfiguration<Template>
{
    public void Configure(EntityTypeBuilder<Template> builder)
    {
        builder.ToTable("Templates");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.Subject).HasMaxLength(Template.SubjectMaxLength).IsRequired();
        builder.Property(t => t.Body).HasMaxLength(Template.BodyMaxLength).IsRequired();

        // One template per status code keeps the choice of template unambiguous.
        builder.HasIndex(t => t.StatusCode).IsUnique();
    }
}
