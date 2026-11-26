using System.Data.Common;
using System.Reflection;
using Dapper;
using Hrms.SharedKernel;

namespace Hrms.Persistence.Sql;

/// <summary>
/// A Dapper connection that is bound to one tenant. Every statement is checked by <see cref="TenantSql"/> and runs with
/// <c>@TenantId</c> set from the tenant context; a caller can neither omit the filter nor supply its own tenant value.
/// Soft delete is not applied here: queries on tenant tables must also filter <c>is_deleted = 0</c>.
/// </summary>
public sealed class TenantDbConnection : IAsyncDisposable
{
    private readonly DbConnection _connection;
    private readonly TenantId _tenantId;

    internal TenantDbConnection(DbConnection connection, TenantId tenantId)
    {
        _connection = connection;
        _tenantId = tenantId;
    }

    public async Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default) =>
        (await _connection.QueryAsync<T>(Prepare(sql, parameters, cancellationToken))).AsList();

    public Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default) =>
        _connection.QuerySingleOrDefaultAsync<T>(Prepare(sql, parameters, cancellationToken));

    /// <summary>Runs an INSERT, UPDATE or DELETE and returns the number of rows affected.</summary>
    public Task<int> ExecuteAsync(string sql, object? parameters = null, CancellationToken cancellationToken = default) =>
        _connection.ExecuteAsync(Prepare(sql, parameters, cancellationToken));

    public ValueTask DisposeAsync() => _connection.DisposeAsync();

    private CommandDefinition Prepare(string sql, object? parameters, CancellationToken cancellationToken)
    {
        TenantSql.Validate(sql);

        // Caller values are copied one by one, so none can slip past the check (a DynamicParameters template hides its members).
        var bound = new DynamicParameters();
        foreach (var (name, value) in Enumerate(parameters))
        {
            if (string.Equals(name, TenantSql.TenantParameter, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"The {TenantSql.TenantParameter} parameter is bound from the tenant context and cannot be supplied by the caller.",
                    nameof(parameters));
            }

            bound.Add(name, value);
        }

        bound.Add(TenantSql.TenantParameter, _tenantId.Value);
        return new CommandDefinition(sql, bound, cancellationToken: cancellationToken);
    }

    private static IEnumerable<(string Name, object? Value)> Enumerate(object? parameters) => parameters switch
    {
        null => [],
        SqlMapper.IDynamicParameters => throw new ArgumentException(
            "Pass an anonymous object or a dictionary; a Dapper parameter bag can carry values this check cannot see.",
            nameof(parameters)),
        IEnumerable<KeyValuePair<string, object?>> pairs => pairs.Select(pair => (pair.Key, pair.Value)),
        _ => parameters.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetIndexParameters().Length == 0)
            .Select(property => (property.Name, property.GetValue(parameters))),
    };
}
