using System.Data.Common;

namespace Hrms.Persistence;

/// <summary>
/// A named, server-wide lock for background jobs, so that of several worker instances only one runs a job at a time. The lock lives as long
/// as the lease: disposing it (or losing the connection, for example when the process dies) releases it, so a crashed worker cannot
/// block the job forever.
/// </summary>
public interface IJobLock
{
    /// <returns>A lease to dispose when the job is done, or null when another instance holds the lock.</returns>
    Task<IAsyncDisposable?> TryAcquireAsync(string name, CancellationToken cancellationToken);
}

internal sealed class MySqlJobLock(DbDataSource dataSource) : IJobLock
{
    private const int MaxNameLength = 100;

    public async Task<IAsyncDisposable?> TryAcquireAsync(string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > MaxNameLength)
        {
            throw new ArgumentException($"A lock name is at most {MaxNameLength} characters.", nameof(name));
        }

        var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            // The database name is part of the lock name, so two databases on one server never block each other.
            await using var command = connection.CreateCommand();
            // Hashed so any database name plus lock name fits MySQL's 64 character limit for lock names.
            command.CommandText = "SELECT GET_LOCK(SHA2(CONCAT(DATABASE(), ':', @name), 256), 0)";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@name";
            parameter.Value = name;
            command.Parameters.Add(parameter);

            var acquired = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) == 1;
            if (acquired)
            {
                return new Lease(connection, name);
            }
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }

        await connection.DisposeAsync();
        return null;
    }

    private sealed class Lease(DbConnection connection, string name) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT RELEASE_LOCK(SHA2(CONCAT(DATABASE(), ':', @name), 256))";
                var parameter = command.CreateParameter();
                parameter.ParameterName = "@name";
                parameter.Value = name;
                command.Parameters.Add(parameter);
                await command.ExecuteScalarAsync();
            }
            finally
            {
                await connection.DisposeAsync(); // closing the connection frees the lock even if the release above failed
            }
        }
    }
}
