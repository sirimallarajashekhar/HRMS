using Hrms.Persistence;
using Hrms.SharedKernel;
using Hrms.Workflow.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Hrms.Workflow.Infrastructure.Persistence;

/// <summary>
/// The workflow module's database. Every entity is tenant-scoped, so the tenant filter, audit stamping and soft delete come from
/// <see cref="TenantDbContext"/>. Users, employees and the records being approved belong to other modules and are plain ids here.
/// </summary>
public sealed class WorkflowDbContext(
    DbContextOptions<WorkflowDbContext> options,
    ITenantContext tenantContext,
    IActorContext actorContext,
    TimeProvider timeProvider) : TenantDbContext(options, tenantContext, actorContext, timeProvider)
{
    public const string MigrationsHistoryTable = "ef_migrations_history_workflow";

    public DbSet<WorkflowDefinition> Definitions => Set<WorkflowDefinition>();

    public DbSet<WorkflowStage> Stages => Set<WorkflowStage>();

    public DbSet<WorkflowInstance> Instances => Set<WorkflowInstance>();

    public DbSet<WorkflowTask> Tasks => Set<WorkflowTask>();

    public DbSet<WorkflowActionLog> ActionLog => Set<WorkflowActionLog>();

    public DbSet<Delegation> Delegations => Set<Delegation>();

    public DbSet<WorkflowOutboxMessage> Outbox => Set<WorkflowOutboxMessage>();

    protected override void ConfigureEntities(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(WorkflowDbContext).Assembly);
}

/// <summary>
/// Used only by `dotnet ef`. Adding a migration never connects; to apply or script against a real server set
/// HRMS_MIGRATIONS_CONNECTION. The tenant context is empty because migrations never run tenant queries.
/// </summary>
internal sealed class WorkflowDbContextFactory : IDesignTimeDbContextFactory<WorkflowDbContext>
{
    public WorkflowDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("HRMS_MIGRATIONS_CONNECTION")
                         ?? "Server=127.0.0.1;Port=3306;Database=hrms;User ID=design;Password=design";

        var options = WorkflowInfrastructureExtensions.UseWorkflowMySql(new DbContextOptionsBuilder<WorkflowDbContext>(), connection);

        return new WorkflowDbContext(
            (DbContextOptions<WorkflowDbContext>)options.Options, new TenantContext(), new SystemActorContext(), TimeProvider.System);
    }
}
