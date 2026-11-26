using Hrms.Core.Domain.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Hrms.Core.Infrastructure.Persistence.Configurations;

internal sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();

        builder.Property(t => t.Code).HasMaxLength(Tenant.CodeMaxLength).IsRequired();
        builder.Property(t => t.Name).HasMaxLength(Tenant.NameMaxLength).IsRequired();

        // Stored as text so reordering the enum can never reinterpret existing rows.
        builder.Property(t => t.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(t => t.CreatedAt).IsRequired();

        // TenantId is a computed view of Id, not a column.
        builder.Ignore(t => t.TenantId);

        builder.HasIndex(t => t.Code).IsUnique().HasDatabaseName("ux_tenant_code");
    }
}
