using Hrms.Identity.Domain.Roles;
using Hrms.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Hrms.Identity.Infrastructure.Persistence.Configurations;

internal sealed class PermissionDefinitionConfiguration : IEntityTypeConfiguration<PermissionDefinition>
{
    public void Configure(EntityTypeBuilder<PermissionDefinition> builder)
    {
        builder.ToTable("permission");
        builder.HasKey(p => p.Code);
        builder.Property(p => p.Code).HasMaxLength(100).IsRequired();
        builder.Property(p => p.Module).HasMaxLength(50).IsRequired();
        builder.HasIndex(p => p.Module).HasDatabaseName("ix_permission_module");
    }
}

internal sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Code).HasMaxLength(Role.CodeMaxLength).IsRequired();
        builder.Property(r => r.Name).HasMaxLength(Role.NameMaxLength).IsRequired();
        builder.Property(r => r.Description).HasMaxLength(Role.DescriptionMaxLength);

        builder.HasUniquePerTenant(r => r.Code, Role.CodeMaxLength);
    }
}

internal sealed class RoleGrantConfiguration : IEntityTypeConfiguration<RoleGrant>
{
    public void Configure(EntityTypeBuilder<RoleGrant> builder)
    {
        builder.ToTable("role_permission");
        builder.HasKey(g => g.Id);
        builder.Property(g => g.PermissionCode).HasMaxLength(100).IsRequired();

        // Rows are soft-deleted, never removed, so a foreign key can only ever Restrict.
        builder.HasOne<Role>().WithMany().HasForeignKey(g => g.RoleId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<PermissionDefinition>().WithMany().HasForeignKey(g => g.PermissionCode).OnDelete(DeleteBehavior.Restrict);

        builder.HasUniqueLiveCombination("ux_role_permission_tenant_role_permission", nameof(RoleGrant.RoleId), nameof(RoleGrant.PermissionCode));
    }
}
