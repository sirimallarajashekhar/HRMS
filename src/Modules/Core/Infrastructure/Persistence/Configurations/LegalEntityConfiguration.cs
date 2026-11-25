using Hrms.Core.Domain.LegalEntities;
using Hrms.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Hrms.Core.Infrastructure.Persistence.Configurations;

internal sealed class LegalEntityConfiguration : IEntityTypeConfiguration<LegalEntity>
{
    public void Configure(EntityTypeBuilder<LegalEntity> builder)
    {
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Code).HasMaxLength(LegalEntity.CodeMaxLength).IsRequired();
        builder.Property(e => e.Name).HasMaxLength(LegalEntity.NameMaxLength).IsRequired();
        builder.Property(e => e.CountryCode).HasMaxLength(2).IsFixedLength().IsRequired();
        builder.Property(e => e.CurrencyCode).HasMaxLength(3).IsFixedLength().IsRequired();
        builder.Property(e => e.CountryPackCode).HasMaxLength(2).IsFixedLength().IsRequired();
        builder.Property(e => e.DefaultLanguage).HasMaxLength(LegalEntity.LanguageMaxLength).IsRequired();
        builder.Property(e => e.TimeZoneId).HasMaxLength(LegalEntity.TimeZoneMaxLength).IsRequired();

        // Codes are unique per tenant among live rows; a soft-deleted entity releases its code.
        builder.HasUniquePerTenant(e => e.Code, LegalEntity.CodeMaxLength);

        // Defence in depth: the domain already rejects it, the database refuses it too.
        builder.ToTable(table => table.HasCheckConstraint(
            "ck_legal_entity_fiscal_year_start_month", "`fiscal_year_start_month` BETWEEN 1 AND 12"));
    }
}
