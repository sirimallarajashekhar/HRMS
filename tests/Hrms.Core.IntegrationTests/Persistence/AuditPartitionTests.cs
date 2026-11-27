using FluentAssertions;
using Hrms.Core.IntegrationTests.Identity;
using Hrms.Persistence;
using Hrms.Persistence.Auditing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace Hrms.Core.IntegrationTests.Persistence;

[Collection(MySqlTestGroup.Name)]
public sealed class AuditPartitionTests(MySqlFixture mysql) : IAsyncLifetime
{
    private string _connectionString = string.Empty;
    private string _database = string.Empty;

    public async Task InitializeAsync()
    {
        _database = $"partitions_{Guid.NewGuid():N}";
        var admin = new MySqlConnectionStringBuilder(mysql.ConnectionString) { UserID = "root", Database = string.Empty };
        await ExecuteAsync(admin.ConnectionString, $"CREATE DATABASE `{_database}` CHARACTER SET utf8mb4");
        admin.Database = _database;
        _connectionString = admin.ConnectionString;
        await AuditTestDb.MigrateAsync(_connectionString);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<List<string>> ColumnAsync(string sql)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetValue(0).ToString()!);
        }

        return values;
    }

    private Task<List<string>> PartitionsAsync() => ColumnAsync(
        "SELECT partition_name FROM information_schema.partitions "
        + "WHERE table_schema = DATABASE() AND table_name = 'audit_log' ORDER BY partition_ordinal_position");

    private static Task InsertAtAsync(string connectionString, string occurredAt) => ExecuteAsync(
        connectionString,
        "INSERT INTO audit_log (id, tenant_id, occurred_at, entity_type, record_id, root_type, root_id, action, changes, actor_type) "
        + $"VALUES (UUID_TO_BIN(UUID()), UUID_TO_BIN(UUID()), '{occurredAt}', 'thing', UUID_TO_BIN(UUID()), 'thing', "
        + "UUID_TO_BIN(UUID()), 1, '[]', 1)");

    private static AuditPartitionMaintenance Maintenance(string connectionString, string today, IJobLock? jobLock = null) => new(
        new AuditMaintenanceConnection(connectionString),
        Options.Create(new AuditPartitionOptions { MonthsAhead = 12 }),
        jobLock ?? new FreeLock(),
        new MovableClock(DateTimeOffset.Parse(today + "T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture)),
        NullLogger<AuditPartitionMaintenance>.Instance);

    [Fact]
    public async Task Run_AddsTheMonthsAhead_MovesRowsOutOfTheCatchAll_AndCanBeRepeated()
    {
        await InsertAtAsync(_connectionString, "2028-03-10 12:00:00");
        var maintenance = Maintenance(_connectionString, "2027-10-15");

        var added = await maintenance.RunAsync(CancellationToken.None);

        added!.Select(p => p.Name).Should().HaveCount(10).And.StartWith("p202801").And.EndWith("p202810");
        (await PartitionsAsync()).Should().EndWith(["p202809", "p202810", "p_future"]);
        (await ColumnAsync("SELECT COUNT(*) FROM audit_log PARTITION (p202803)")).Should().Equal("1");
        (await ColumnAsync("SELECT COUNT(*) FROM audit_log")).Should().Equal("1");
        (await maintenance.RunAsync(CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task Run_WhenAnotherInstanceHoldsTheLock_DoesNothing()
    {
        var before = await PartitionsAsync();

        var added = await Maintenance(_connectionString, "2027-10-15", new HeldLock()).RunAsync(CancellationToken.None);

        added.Should().BeNull();
        (await PartitionsAsync()).Should().Equal(before);
    }

    [Fact]
    public async Task Run_AsAUserThatMayOnlyReadAndInsert_Fails_AndAuditWritesStillWork()
    {
        var user = $"app_{Guid.NewGuid():N}"[..20];
        await ExecuteAsync(_connectionString, $"CREATE USER '{user}'@'%' IDENTIFIED BY 'Test-only-password-1'");
        await ExecuteAsync(_connectionString, $"GRANT SELECT, INSERT ON `{_database}`.`audit_log` TO '{user}'@'%'");
        var limited = new MySqlConnectionStringBuilder(_connectionString) { UserID = user, Password = "Test-only-password-1" }
            .ConnectionString;

        var run = () => Maintenance(limited, "2027-10-15").RunAsync(CancellationToken.None);

        await run.Should().ThrowAsync<MySqlException>();
        await InsertAtAsync(limited, "2028-03-10 12:00:00");
        var delete = () => ExecuteAsync(limited, "DELETE FROM audit_log");
        await delete.Should().ThrowAsync<MySqlException>("these are the grants that make the log append-only in the database");
    }

    private sealed class FreeLock : IJobLock
    {
        public Task<IAsyncDisposable?> TryAcquireAsync(string name, CancellationToken cancellationToken) =>
            Task.FromResult<IAsyncDisposable?>(new Lease());

        private sealed class Lease : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class HeldLock : IJobLock
    {
        public Task<IAsyncDisposable?> TryAcquireAsync(string name, CancellationToken cancellationToken) =>
            Task.FromResult<IAsyncDisposable?>(null);
    }
}
