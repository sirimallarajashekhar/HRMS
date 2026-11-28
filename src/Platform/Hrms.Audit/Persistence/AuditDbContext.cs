using Hrms.Persistence;
using Hrms.Persistence.Auditing;
using Hrms.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Hrms.Audit.Persistence;

/// <summary>
/// Owns the audit_log table: its migrations create it, and history is read through this context. Rows are written by whichever module
/// context saves the change (see <see cref="TenantDbContext"/>), in the same transaction.
/// </summary>
public sealed class AuditDbContext(
    DbContextOptions<AuditDbContext> options,
    ITenantContext tenantContext,
    IActorContext actorContext,
    TimeProvider timeProvider) : TenantDbContext(options, tenantContext, actorContext, timeProvider)
{
    public const string MigrationsHistoryTable = "ef_migrations_history_audit";

    protected override bool OwnsAuditTable => true;

    public DbSet<AuditEntry> Entries => Set<AuditEntry>();

    public static DbContextOptionsBuilder UseAuditMySql(DbContextOptionsBuilder options, string? mysqlConnectionString)
    {
        if (string.IsNullOrWhiteSpace(mysqlConnectionString))
        {
            throw new InvalidOperationException("The connection string 'ConnectionStrings:Mysql' is not configured.");
        }

        return options.UseMySql(
            MySqlConnectionStrings.ForHrms(mysqlConnectionString),
            new MySqlServerVersion(new Version(8, 4, 0)),
            mysql => mysql.MigrationsHistoryTable(MigrationsHistoryTable));
    }
}

/// <summary>
/// Used only by `dotnet ef`. Adding a migration never connects; to apply or script against a real server set
/// HRMS_MIGRATIONS_CONNECTION.
/// </summary>
internal sealed class AuditDbContextFactory : IDesignTimeDbContextFactory<AuditDbContext>
{
    public AuditDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("HRMS_MIGRATIONS_CONNECTION")
                         ?? "Server=127.0.0.1;Port=3306;Database=hrms;User ID=design;Password=design";
        var options = AuditDbContext.UseAuditMySql(new DbContextOptionsBuilder<AuditDbContext>(), connection);

        return new AuditDbContext(
            (DbContextOptions<AuditDbContext>)options.Options, new TenantContext(), new SystemActorContext(), TimeProvider.System);
    }
}
