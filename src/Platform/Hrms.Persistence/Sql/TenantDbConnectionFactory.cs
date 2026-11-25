using System.Data.Common;
using Dapper;
using Hrms.SharedKernel;

namespace Hrms.Persistence.Sql;

/// <summary>
/// Opens <see cref="TenantDbConnection"/>s for the current tenant. The data source's connection string must use
/// GuidFormat=Binary16 (ids are binary(16)) and DateTimeKind=Utc (timestamps are UTC), matching the EF configuration.
/// </summary>
public sealed class TenantDbConnectionFactory
{
    private readonly DbDataSource _dataSource;
    private readonly ITenantContext _tenantContext;

    static TenantDbConnectionFactory() =>
        // The one process-wide Dapper setting: map snake_case columns to PascalCase properties. Idempotent.
        DefaultTypeMap.MatchNamesWithUnderscores = true;

    public TenantDbConnectionFactory(DbDataSource dataSource, ITenantContext tenantContext)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(tenantContext);
        _dataSource = dataSource;
        _tenantContext = tenantContext;
    }

    /// <summary>Opens a connection bound to the current tenant; throws <see cref="TenantNotResolvedException"/> when there is none.</summary>
    public async ValueTask<TenantDbConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var tenantId = _tenantContext.Require();
        var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        return new TenantDbConnection(connection, tenantId);
    }
}
