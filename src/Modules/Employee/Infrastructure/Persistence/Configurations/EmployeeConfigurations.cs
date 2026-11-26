using Hrms.Employee.Domain;
using Hrms.Persistence;
using Hrms.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Hrms.Employee.Infrastructure.Persistence.Configurations;

internal sealed class EmployeeRecordConfiguration : IEntityTypeConfiguration<EmployeeRecord>
{
    public void Configure(EntityTypeBuilder<EmployeeRecord> builder)
    {
        builder.ToTable("employee");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Code).HasMaxLength(EmployeeRecord.CodeMaxLength).IsRequired();
        builder.HasUniquePerTenant(e => e.Code, EmployeeRecord.CodeMaxLength);
        builder.HasIndex(nameof(TenantEntity.TenantId), nameof(EmployeeRecord.JoiningDate)).HasDatabaseName("ix_employee_tenant_joining_date");
        builder.HasIndex(nameof(TenantEntity.TenantId), nameof(EmployeeRecord.LastWorkingDay)).HasDatabaseName("ix_employee_tenant_last_working_day");
    }
}

internal sealed class EmployeeJobConfiguration : IEntityTypeConfiguration<EmployeeJob>
{
    public void Configure(EntityTypeBuilder<EmployeeJob> builder)
    {
        builder.ToTable("employee_job");
        builder.HasKey(e => e.Id);
        builder.Ignore(e => e.Period);
        builder.Property(e => e.Reason).HasConversion<int>().IsRequired();
        builder.HasOne<EmployeeRecord>().WithMany().HasForeignKey(e => e.EmployeeId).OnDelete(DeleteBehavior.Restrict);

        // One period per employee and start date: the backstop for two racing changes recording the same period.
        builder.HasIndex(nameof(TenantEntity.TenantId), nameof(EmployeeJob.EmployeeId), nameof(EmployeeJob.EffectiveFrom))
            .IsUnique()
            .HasDatabaseName("ux_employee_job_tenant_employee_from");

        // The access paths: reports of a manager, people in a unit, location or legal entity, and the daily job's "became effective" scan.
        builder.HasIndex(nameof(TenantEntity.TenantId), nameof(EmployeeJob.ManagerId), nameof(EmployeeJob.EffectiveFrom)).HasDatabaseName("ix_employee_job_tenant_manager_from");
        builder.HasIndex(nameof(TenantEntity.TenantId), nameof(EmployeeJob.OrgUnitId), nameof(EmployeeJob.EffectiveFrom)).HasDatabaseName("ix_employee_job_tenant_org_unit_from");
        builder.HasIndex(nameof(TenantEntity.TenantId), nameof(EmployeeJob.LocationId), nameof(EmployeeJob.EffectiveFrom)).HasDatabaseName("ix_employee_job_tenant_location_from");
        builder.HasIndex(nameof(TenantEntity.TenantId), nameof(EmployeeJob.LegalEntityId), nameof(EmployeeJob.EffectiveFrom)).HasDatabaseName("ix_employee_job_tenant_legal_entity_from");
        builder.HasIndex(nameof(TenantEntity.TenantId), nameof(EmployeeJob.EffectiveFrom), nameof(EmployeeJob.EventPublishedAt)).HasDatabaseName("ix_employee_job_tenant_from_published");

        builder.ToTable(table => table.HasCheckConstraint(
            "ck_employee_job_notice_period", $"`notice_period_days` BETWEEN 0 AND {EmployeeJob.MaxNoticePeriodDays}"));
    }
}

internal sealed class EmployeePersonalConfiguration : IEntityTypeConfiguration<EmployeePersonal>
{
    public void Configure(EntityTypeBuilder<EmployeePersonal> builder)
    {
        builder.ToTable("employee_personal");
        builder.HasKey(e => e.Id);
        builder.Ignore(e => e.DisplayName);
        builder.Property(e => e.FirstName).HasMaxLength(EmployeePersonal.NameMaxLength).IsRequired();
        builder.Property(e => e.MiddleName).HasMaxLength(EmployeePersonal.NameMaxLength);
        builder.Property(e => e.LastName).HasMaxLength(EmployeePersonal.NameMaxLength).IsRequired();
        builder.Property(e => e.Gender).HasConversion<int>().IsRequired();
        builder.Property(e => e.PersonalEmail).HasMaxLength(EmployeePersonal.EmailMaxLength);
        builder.Property(e => e.Phone).HasMaxLength(EmployeePersonal.PhoneMaxLength);
        builder.HasOne<EmployeeRecord>().WithMany().HasForeignKey(e => e.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasUniqueLiveCombination("ux_employee_personal_tenant_employee", nameof(EmployeePersonal.EmployeeId));

        // Name search is a prefix match, which this index serves.
        builder.HasIndex(nameof(TenantEntity.TenantId), nameof(EmployeePersonal.LastName), nameof(EmployeePersonal.FirstName)).HasDatabaseName("ix_employee_personal_tenant_name");
    }
}

internal sealed class EmployeeCodeRuleConfiguration : IEntityTypeConfiguration<EmployeeCodeRule>
{
    public void Configure(EntityTypeBuilder<EmployeeCodeRule> builder)
    {
        builder.ToTable("employee_code_rule");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Prefix).HasMaxLength(EmployeeCodeRule.PrefixMaxLength).IsRequired();
        builder.HasUniqueLiveCombination("ux_employee_code_rule_tenant_legal_entity", nameof(EmployeeCodeRule.LegalEntityId));
        builder.ToTable(table =>
        {
            table.HasCheckConstraint("ck_employee_code_rule_width", $"`width` BETWEEN 1 AND {EmployeeCodeRule.MaxWidth}");
            table.HasCheckConstraint("ck_employee_code_rule_next_number", "`next_number` >= 1");
        });
    }
}

internal sealed class SeparationConfiguration : IEntityTypeConfiguration<Separation>
{
    public void Configure(EntityTypeBuilder<Separation> builder)
    {
        builder.ToTable("employee_separation");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Type).HasConversion<int>().IsRequired();
        builder.Property(e => e.Reason).HasMaxLength(Separation.ReasonMaxLength);
        builder.HasOne<EmployeeRecord>().WithMany().HasForeignKey(e => e.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasUniqueLiveCombination("ux_employee_separation_tenant_employee", nameof(Separation.EmployeeId));
        builder.HasIndex(nameof(TenantEntity.TenantId), nameof(Separation.LastWorkingDay), nameof(Separation.EventPublishedAt)).HasDatabaseName("ix_employee_separation_tenant_last_day_published");
    }
}

internal sealed class StatutoryIdConfiguration : IEntityTypeConfiguration<StatutoryId>
{
    public void Configure(EntityTypeBuilder<StatutoryId> builder)
    {
        builder.ToTable("employee_statutory_id");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.IdType).HasMaxLength(StatutoryId.IdTypeMaxLength).IsRequired();
        builder.Property(e => e.ProtectedValue).HasMaxLength(500).IsRequired();
        builder.HasOne<EmployeeRecord>().WithMany().HasForeignKey(e => e.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasUniqueLiveCombination("ux_employee_statutory_id_tenant_employee_type", nameof(StatutoryId.EmployeeId), nameof(StatutoryId.IdType));
    }
}

internal sealed class EmployeeDocumentConfiguration : IEntityTypeConfiguration<EmployeeDocument>
{
    public void Configure(EntityTypeBuilder<EmployeeDocument> builder)
    {
        builder.ToTable("employee_document");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.DocumentType).HasMaxLength(EmployeeDocument.TypeMaxLength).IsRequired();
        builder.Property(e => e.Title).HasMaxLength(EmployeeDocument.TitleMaxLength).IsRequired();
        builder.Property(e => e.FileKey).HasMaxLength(EmployeeDocument.FileKeyMaxLength);
        builder.HasOne<EmployeeRecord>().WithMany().HasForeignKey(e => e.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(nameof(TenantEntity.TenantId), nameof(EmployeeDocument.EmployeeId)).HasDatabaseName("ix_employee_document_tenant_employee");
        builder.HasIndex(nameof(TenantEntity.TenantId), nameof(EmployeeDocument.ExpiresOn)).HasDatabaseName("ix_employee_document_tenant_expires_on");
    }
}

internal sealed class EmployeeDeviceConfiguration : IEntityTypeConfiguration<EmployeeDevice>
{
    public void Configure(EntityTypeBuilder<EmployeeDevice> builder)
    {
        builder.ToTable("employee_device");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.DeviceId).HasMaxLength(EmployeeDevice.DeviceIdMaxLength).IsRequired();
        builder.Property(e => e.Name).HasMaxLength(EmployeeDevice.NameMaxLength);
        builder.Property(e => e.Platform).HasConversion<int>().IsRequired();
        builder.Property(e => e.Status).HasConversion<int>().IsRequired();
        builder.HasOne<EmployeeRecord>().WithMany().HasForeignKey(e => e.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasUniquePerTenant(e => e.DeviceId, EmployeeDevice.DeviceIdMaxLength);
        builder.HasIndex(nameof(TenantEntity.TenantId), nameof(EmployeeDevice.EmployeeId)).HasDatabaseName("ix_employee_device_tenant_employee");
    }
}

internal sealed class EmployeeConsentConfiguration : IEntityTypeConfiguration<EmployeeConsent>
{
    public void Configure(EntityTypeBuilder<EmployeeConsent> builder)
    {
        builder.ToTable("employee_consent");
        builder.HasKey(e => e.Id);
        builder.Ignore(e => e.IsActive);
        builder.Property(e => e.ConsentType).HasMaxLength(EmployeeConsent.TypeMaxLength).IsRequired();
        builder.Property(e => e.Version).HasMaxLength(EmployeeConsent.VersionMaxLength).IsRequired();
        builder.Property(e => e.Source).HasMaxLength(EmployeeConsent.SourceMaxLength);
        builder.HasOne<EmployeeRecord>().WithMany().HasForeignKey(e => e.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(nameof(TenantEntity.TenantId), nameof(EmployeeConsent.EmployeeId), nameof(EmployeeConsent.ConsentType)).HasDatabaseName("ix_employee_consent_tenant_employee_type");
    }
}

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("employee_outbox");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.EventType).HasMaxLength(OutboxMessage.TypeMaxLength).IsRequired();
        builder.Property(e => e.Payload).HasColumnType("text").IsRequired();
        builder.Property(e => e.LastError).HasMaxLength(500);

        // The relay reads "oldest unprocessed first" per tenant.
        builder.HasIndex(nameof(TenantEntity.TenantId), nameof(OutboxMessage.ProcessedAt), nameof(OutboxMessage.OccurredAt)).HasDatabaseName("ix_employee_outbox_tenant_processed_occurred");
    }
}
