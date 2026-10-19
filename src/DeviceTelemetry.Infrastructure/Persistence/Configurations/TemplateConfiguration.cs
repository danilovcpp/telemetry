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

        builder.Property(t => t.Language).HasMaxLength(Template.LanguageMaxLength);

        // One template per (status code, language) keeps the choice of template unambiguous; NULLS NOT DISTINCT also
        // allows only one language-neutral template per status code (PostgreSQL 15+).
        builder.HasIndex(t => new { t.StatusCode, t.Language }).IsUnique().AreNullsDistinct(false);
    }
}
