using Hrms.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Hrms.Core.Infrastructure.Persistence;

/// <summary>
/// Used only by `dotnet ef`. Adding a migration never connects; to apply or script against a real server set
/// HRMS_MIGRATIONS_CONNECTION. The tenant context is empty because migrations never run tenant queries.
/// </summary>
internal sealed class CoreDbContextFactory : IDesignTimeDbContextFactory<CoreDbContext>
{
    public const string ConnectionVariable = "HRMS_MIGRATIONS_CONNECTION";

    public CoreDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable(ConnectionVariable)
                         ?? "Server=127.0.0.1;Port=3306;Database=hrms;User ID=design;Password=design";

        var options = CoreInfrastructureExtensions
            .UseCoreMySql(new DbContextOptionsBuilder<CoreDbContext>(), connection);

        return new CoreDbContext(
            (DbContextOptions<CoreDbContext>)options.Options,
            new TenantContext(),
            new SystemActorContext(),
            TimeProvider.System);
    }
}
