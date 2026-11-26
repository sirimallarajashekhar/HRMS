using Hrms.Persistence;
using Hrms.SharedKernel;
using Hrms.Workflow.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Hrms.Workflow.Infrastructure.Persistence.Configurations;

internal sealed class WorkflowDefinitionConfiguration : IEntityTypeConfiguration<WorkflowDefinition>
{
    public void Configure(EntityTypeBuilder<WorkflowDefinition> builder)
    {
        builder.ToTable("workflow_def");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.WorkflowKey).HasMaxLength(100).IsRequired();
        builder.Property(e => e.Name).HasMaxLength(WorkflowDefinition.NameMaxLength).IsRequired();
        builder.Property(e => e.Status).HasConversion<string>().HasMaxLength(16).IsRequired();

        // One live row per key and version: two people creating a draft at once cannot both win.
        builder.HasUniqueLiveCombination("ux_workflow_def_tenant_key_version", nameof(WorkflowDefinition.WorkflowKey), nameof(WorkflowDefinition.Version));

        // Serves "the published version in force for this key today".
        builder.HasIndex(nameof(TenantEntity.TenantId), nameof(WorkflowDefinition.WorkflowKey), nameof(WorkflowDefinition.Status), nameof(WorkflowDefinition.EffectiveFrom))
            .HasDatabaseName("ix_workflow_def_tenant_key_status_from");
    }
}

internal sealed class WorkflowStageConfiguration : IEntityTypeConfiguration<WorkflowStage>
{
    public void Configure(EntityTypeBuilder<WorkflowStage> builder)
    {
        builder.ToTable("workflow_stage_def");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Name).HasMaxLength(WorkflowStage.NameMaxLength).IsRequired();
        builder.Property(e => e.ApproverJson).HasMaxLength(500).IsRequired();
        builder.Property(e => e.FallbackJson).HasMaxLength(500);
        builder.Property(e => e.EscalationJson).HasMaxLength(500);
        builder.Property(e => e.ConditionJson).HasColumnType("text");
        builder.Property(e => e.Mode).HasConversion<string>().HasMaxLength(8).IsRequired();
        builder.Property(e => e.OnTimeout).HasConversion<string>().HasMaxLength(16);
        builder.Ignore(e => e.Approver);
        builder.Ignore(e => e.Fallback);
        builder.Ignore(e => e.Escalation);

        builder.HasOne<WorkflowDefinition>().WithMany().HasForeignKey(e => e.DefinitionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasUniqueLiveCombination("ux_workflow_stage_def_tenant_def_order", nameof(WorkflowStage.DefinitionId), nameof(WorkflowStage.StageOrder));
    }
}

internal sealed class WorkflowInstanceConfiguration : IEntityTypeConfiguration<WorkflowInstance>
{
    public void Configure(EntityTypeBuilder<WorkflowInstance> builder)
    {
        builder.ToTable("workflow_instance");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.WorkflowKey).HasMaxLength(100).IsRequired();
        builder.Property(e => e.EntityType).HasMaxLength(64).IsRequired();
        builder.Property(e => e.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(e => e.PayloadJson).HasColumnType("text").IsRequired();
        builder.Property(e => e.BlockedReason).HasMaxLength(WorkflowInstance.ReasonMaxLength);
        builder.Property(e => e.StartFingerprint).HasMaxLength(64).IsRequired();
        builder.Ignore(e => e.IsOpen);

        builder.HasOne<WorkflowDefinition>().WithMany().HasForeignKey(e => e.DefinitionId).OnDelete(DeleteBehavior.Restrict);

        // One workflow per key and request, ever. Instances are never deleted, so a plain unique index is enough; it is what makes
        // a repeated or racing start harmless.
        builder.HasIndex(nameof(TenantEntity.TenantId), nameof(WorkflowInstance.WorkflowKey), nameof(WorkflowInstance.EntityId))
            .IsUnique()
            .HasDatabaseName("ux_workflow_instance_tenant_key_entity");
        builder.HasIndex(nameof(TenantEntity.TenantId), nameof(WorkflowInstance.RequesterUserId), nameof(WorkflowInstance.Status))
            .HasDatabaseName("ix_workflow_instance_tenant_requester_status");
        builder.HasIndex(nameof(TenantEntity.TenantId), nameof(WorkflowInstance.EntityType), nameof(WorkflowInstance.EntityId))
            .HasDatabaseName("ix_workflow_instance_tenant_record");
        builder.HasIndex(nameof(TenantEntity.TenantId), nameof(WorkflowInstance.Status), nameof(TenantEntity.CreatedAt))
            .HasDatabaseName("ix_workflow_instance_tenant_status_created");
        builder.HasIndex(nameof(TenantEntity.TenantId), nameof(WorkflowInstance.SubjectEmployeeId))
            .HasDatabaseName("ix_workflow_instance_tenant_subject");
    }
}

internal sealed class WorkflowTaskConfiguration : IEntityTypeConfiguration<WorkflowTask>
{
    public void Configure(EntityTypeBuilder<WorkflowTask> builder)
    {
        builder.ToTable("workflow_task");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(e => e.Origin).HasConversion<string>().HasMaxLength(16).IsRequired();

        builder.HasOne<WorkflowInstance>().WithMany().HasForeignKey(e => e.InstanceId).OnDelete(DeleteBehavior.Restrict);

        // The inbox: my open tasks (and those of the people who delegated to me).
        builder.HasIndex(nameof(TenantEntity.TenantId), nameof(WorkflowTask.AssigneeUserId), nameof(WorkflowTask.Status), nameof(WorkflowTask.DueAt))
            .HasDatabaseName("ix_workflow_task_tenant_assignee_status_due");

        // The deadline scan. overdue_handled_at comes before due_at so tasks already dealt with are not read again on every tick.
        builder.HasIndex(nameof(TenantEntity.TenantId), nameof(WorkflowTask.Status), nameof(WorkflowTask.OverdueHandledAt), nameof(WorkflowTask.DueAt))
            .HasDatabaseName("ix_workflow_task_tenant_status_handled_due");
        builder.HasIndex(nameof(TenantEntity.TenantId), nameof(WorkflowTask.InstanceId))
            .HasDatabaseName("ix_workflow_task_tenant_instance");
        builder.HasIndex(nameof(TenantEntity.TenantId), nameof(WorkflowTask.DecidedByUserId))
            .HasDatabaseName("ix_workflow_task_tenant_decided_by");
    }
}

internal sealed class WorkflowActionLogConfiguration : IEntityTypeConfiguration<WorkflowActionLog>
{
    public void Configure(EntityTypeBuilder<WorkflowActionLog> builder)
    {
        builder.ToTable("workflow_action_log");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Action).HasConversion<string>().HasMaxLength(24).IsRequired();
        builder.Property(e => e.Comment).HasMaxLength(WorkflowActionLog.CommentMaxLength);

        builder.HasOne<WorkflowInstance>().WithMany().HasForeignKey(e => e.InstanceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(nameof(TenantEntity.TenantId), nameof(WorkflowActionLog.InstanceId), nameof(WorkflowActionLog.Sequence))
            .IsUnique()
            .HasDatabaseName("ux_workflow_action_log_tenant_instance_sequence");
    }
}

internal sealed class DelegationConfiguration : IEntityTypeConfiguration<Delegation>
{
    public void Configure(EntityTypeBuilder<Delegation> builder)
    {
        builder.ToTable("delegation");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.WorkflowKey).HasMaxLength(100).IsRequired();

        builder.HasIndex(nameof(TenantEntity.TenantId), nameof(Delegation.DelegateUserId), nameof(Delegation.EndsOn))
            .HasDatabaseName("ix_delegation_tenant_delegate_ends");
        builder.HasIndex(nameof(TenantEntity.TenantId), nameof(Delegation.DelegatorUserId), nameof(Delegation.EndsOn))
            .HasDatabaseName("ix_delegation_tenant_delegator_ends");
    }
}

internal sealed class WorkflowOutboxMessageConfiguration : IEntityTypeConfiguration<WorkflowOutboxMessage>
{
    public void Configure(EntityTypeBuilder<WorkflowOutboxMessage> builder)
    {
        builder.ToTable("workflow_outbox");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.EventType).HasMaxLength(64).IsRequired();
        builder.Property(e => e.Payload).HasColumnType("text").IsRequired();
        builder.Property(e => e.LastError).HasMaxLength(WorkflowOutboxMessage.ErrorMaxLength);
        builder.Ignore(e => e.IsExhausted);

        builder.HasOne<WorkflowInstance>().WithMany().HasForeignKey(e => e.InstanceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(nameof(TenantEntity.TenantId), nameof(WorkflowOutboxMessage.ProcessedAt), nameof(WorkflowOutboxMessage.NextAttemptAt))
            .HasDatabaseName("ix_workflow_outbox_tenant_processed_next");

        // Serves "is an earlier message of this instance still waiting".
        builder.HasIndex(nameof(TenantEntity.TenantId), nameof(WorkflowOutboxMessage.InstanceId), nameof(WorkflowOutboxMessage.Sequence))
            .IsUnique()
            .HasDatabaseName("ux_workflow_outbox_tenant_instance_sequence");
    }
}
