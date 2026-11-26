using Hrms.Identity.Domain.Roles;
using Hrms.Identity.Domain.Security;
using Hrms.Identity.Domain.Users;
using Hrms.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Hrms.Identity.Infrastructure.Persistence.Configurations;

internal sealed class UserAccountConfiguration : IEntityTypeConfiguration<UserAccount>
{
    public void Configure(EntityTypeBuilder<UserAccount> builder)
    {
        builder.HasKey(u => u.Id);
        builder.Property(u => u.Email).HasMaxLength(UserAccount.EmailMaxLength).IsRequired();
        builder.Property(u => u.DisplayName).HasMaxLength(UserAccount.DisplayNameMaxLength).IsRequired();

        // A PBKDF2 hash in the ASP.NET Core Identity format is about 84 characters; the column leaves room for stronger formats.
        builder.Property(u => u.PasswordHash).HasMaxLength(512).IsRequired();
        builder.Property(u => u.SecurityStamp).HasMaxLength(64).IsRequired();

        // The TOTP secret is stored encrypted ("v1.{keyId}.{base64}"), never as plain text.
        builder.Property(u => u.MfaSecretProtected).HasMaxLength(400);
        builder.Ignore(u => u.HasPendingMfaEnrollment);

        // Stored as text so reordering the enum can never reinterpret existing rows.
        builder.Property(u => u.Status).HasConversion<string>().HasMaxLength(16).IsRequired();

        // The sign-in name is unique per tenant among live accounts, so a removed account releases its email.
        builder.HasUniquePerTenant(u => u.Email, UserAccount.EmailMaxLength);
    }
}

internal sealed class UserRoleScopeConfiguration : IEntityTypeConfiguration<UserRoleScope>
{
    public void Configure(EntityTypeBuilder<UserRoleScope> builder)
    {
        builder.HasKey(s => s.Id);
        builder.Property(s => s.ScopeType).HasConversion<string>().HasMaxLength(16).IsRequired();

        builder.HasOne<UserAccount>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Role>().WithMany().HasForeignKey(s => s.RoleId).OnDelete(DeleteBehavior.Restrict);

        builder.HasUniqueLiveCombination(
            "ux_user_role_scope_tenant_grant",
            nameof(UserRoleScope.UserId), nameof(UserRoleScope.RoleId), nameof(UserRoleScope.ScopeType), nameof(UserRoleScope.ScopeValue));
    }
}

internal sealed class TenantSecurityPolicyConfiguration : IEntityTypeConfiguration<TenantSecurityPolicy>
{
    public void Configure(EntityTypeBuilder<TenantSecurityPolicy> builder)
    {
        builder.HasKey(p => p.Id);
        builder.Ignore(p => p.Period);

        // Looked up as "the policy in force on a date" for one tenant.
        builder.HasIndex(nameof(TenantSecurityPolicy.TenantId), nameof(TenantSecurityPolicy.EffectiveFrom))
            .HasDatabaseName("ix_tenant_security_policy_tenant_effective_from");

        builder.ToTable(table =>
        {
            table.HasCheckConstraint("ck_tenant_security_policy_min_length", "`min_password_length` BETWEEN 8 AND 128");
            table.HasCheckConstraint("ck_tenant_security_policy_attempts", "`max_failed_attempts` BETWEEN 3 AND 20");
            table.HasCheckConstraint("ck_tenant_security_policy_lockout", "`lockout_minutes` BETWEEN 1 AND 1440");
        });
    }
}

internal sealed class RecoveryCodeConfiguration : IEntityTypeConfiguration<RecoveryCode>
{
    public void Configure(EntityTypeBuilder<RecoveryCode> builder)
    {
        builder.ToTable("user_recovery_code");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.CodeHash).HasMaxLength(64).IsFixedLength().IsRequired();
        builder.Ignore(c => c.IsUsed);

        builder.HasOne<UserAccount>().WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasUniqueLiveCombination("ux_user_recovery_code_tenant_user_hash", nameof(RecoveryCode.UserId), nameof(RecoveryCode.CodeHash));
    }
}
