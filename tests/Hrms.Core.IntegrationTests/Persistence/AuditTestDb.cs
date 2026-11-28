using Hrms.Audit.Persistence;
using Hrms.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Hrms.Core.IntegrationTests.Persistence;

/// <summary>Every test database that holds audited tables needs the audit table too, as a deployment does.</summary>
public static class AuditTestDb
{
    public static async Task MigrateAsync(string connectionString)
    {
        var options = (DbContextOptions<AuditDbContext>)AuditDbContext
            .UseAuditMySql(new DbContextOptionsBuilder<AuditDbContext>(), connectionString)
            .Options;
        await using var context = new AuditDbContext(options, new TenantContext(), new FixedActor(null), TimeProvider.System);
        await context.Database.MigrateAsync();
    }
}
