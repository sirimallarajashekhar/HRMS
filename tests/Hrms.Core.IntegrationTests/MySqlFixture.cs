using System.Collections.Concurrent;
using MySqlConnector;
using Testcontainers.MySql;

namespace Hrms.Core.IntegrationTests;

/// <summary>
/// The MySQL 8.4 server the tests run against. By default each fixture starts its own container (what CI does). When the
/// <see cref="LocalServerVariable"/> environment variable holds a connection string for the root user of a local server, fixtures
/// share that server instead: each works in databases of its own and drops what it created when it is disposed. Use a server kept
/// for development and tests. A run that is killed leaves its databases behind.
/// </summary>
public sealed class MySqlFixture : IAsyncLifetime
{
    public const string LocalServerVariable = "HRMS_TEST_MYSQL";

    private readonly string? _localServer = Environment.GetEnvironmentVariable(LocalServerVariable);
    private readonly ConcurrentQueue<string> _databases = new();
    private readonly ConcurrentQueue<string> _users = new();
    private MySqlContainer? _container;
    private string _connectionString = string.Empty;

    /// <summary>A connection string with a default database that belongs to this fixture.</summary>
    public string ConnectionString => _connectionString;

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(_localServer))
        {
            _container = new MySqlBuilder("mysql:8.4")
                .WithCommand("--character-set-server=utf8mb4", "--collation-server=utf8mb4_0900_ai_ci", "--default-time-zone=+00:00", "--max-connections=1000")
                .Build();
            await _container.StartAsync();
            _connectionString = _container.GetConnectionString();
            return;
        }

        await CheckLocalServerAsync();
        _connectionString = await CreateDatabaseAsync();
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
            return;
        }

        // Only names this fixture generated are dropped, so nothing else on the server can be touched.
        while (_users.TryDequeue(out var user))
        {
            await ExecuteAsRootAsync($"DROP USER IF EXISTS '{user}'@'%'");
        }

        while (_databases.TryDequeue(out var database))
        {
            await ExecuteAsRootAsync($"DROP DATABASE IF EXISTS `{database}`");
        }
    }

    /// <summary>
    /// Creates an empty database and returns a root connection string for it (the ordinary test user cannot create databases).
    /// </summary>
    public async Task<string> CreateDatabaseAsync()
    {
        // At most 45 characters: EF names its migration lock "__{database}_EFMigrationsLock" and MySQL allows 64 for a lock name.
        var database = $"hrmstest_{Guid.NewGuid():N}";
        await ExecuteAsRootAsync($"CREATE DATABASE `{database}` CHARACTER SET utf8mb4");
        _databases.Enqueue(database);
        return new MySqlConnectionStringBuilder(RootConnectionString()) { Database = database }.ConnectionString;
    }

    /// <summary>A new, unused user name that is dropped with this fixture. The caller creates the user as <c>'name'@'%'</c>.</summary>
    public string NewUserName()
    {
        var user = $"hrmstest_{Guid.NewGuid():N}"[..24];
        _users.Enqueue(user);
        return user;
    }

    private string RootConnectionString() =>
        new MySqlConnectionStringBuilder(_container?.GetConnectionString() ?? _localServer!) { UserID = "root", Database = string.Empty }
            .ConnectionString;

    private async Task ExecuteAsRootAsync(string sql)
    {
        await using var connection = new MySqlConnection(RootConnectionString());
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Refuses a server that differs from the container in a way that would change what the tests prove.</summary>
    private async Task CheckLocalServerAsync()
    {
        // The tests reconnect as root with the password given, so any other user would fail later with a misleading error.
        if (!string.Equals(new MySqlConnectionStringBuilder(_localServer!).UserID, "root", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{LocalServerVariable} must connect as the root user.");
        }

        await using var connection = new MySqlConnection(RootConnectionString());
        await connection.OpenAsync();
        await using var command = new MySqlCommand(
            "SELECT VERSION(), @@global.character_set_server, @@global.collation_server, @@global.time_zone, "
            + "@@global.transaction_isolation, @@global.max_connections",
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();

        var problems = new List<string>();
        void Expect(string what, bool ok, object actual, string wanted)
        {
            if (!ok)
            {
                problems.Add($"{what} is '{actual}', expected {wanted}");
            }
        }

        var version = reader.GetString(0);
        Expect("version", version.StartsWith("8.4.", StringComparison.Ordinal), version, "8.4.x");
        Expect("character_set_server", reader.GetString(1) == "utf8mb4", reader.GetString(1), "utf8mb4");
        Expect("collation_server", reader.GetString(2) == "utf8mb4_0900_ai_ci", reader.GetString(2), "utf8mb4_0900_ai_ci");
        Expect("time_zone", reader.GetString(3) == "+00:00", reader.GetString(3), "+00:00");
        Expect("transaction_isolation", reader.GetString(4) == "REPEATABLE-READ", reader.GetString(4), "REPEATABLE-READ");
        var maxConnections = Convert.ToInt64(reader.GetValue(5), System.Globalization.CultureInfo.InvariantCulture);
        Expect("max_connections", maxConnections >= 1000, maxConnections, "at least 1000");

        if (problems.Count > 0)
        {
            // The connection string holds a password, so it is never part of the message.
            throw new InvalidOperationException($"The server in {LocalServerVariable} cannot be used: {string.Join("; ", problems)}.");
        }
    }
}
