using FluentAssertions;
using Hrms.Audit.Persistence;
using Hrms.SharedKernel;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;

namespace Hrms.Core.IntegrationTests.Persistence;

[Collection(MySqlTestGroup.Name)]
public sealed class AuditSchemaTests(MySqlFixture mysql) : IAsyncLifetime
{
    private string _connectionString = string.Empty;

    public async Task InitializeAsync()
    {
        var database = $"audit_{Guid.NewGuid():N}";
        var admin = new MySqlConnectionStringBuilder(mysql.ConnectionString) { UserID = "root", Database = string.Empty };
        await using (var connection = new MySqlConnection(admin.ConnectionString))
        {
            await connection.OpenAsync();
            await using var create = new MySqlCommand($"CREATE DATABASE `{database}` CHARACTER SET utf8mb4", connection);
            await create.ExecuteNonQueryAsync();
        }

        admin.Database = database;
        _connectionString = admin.ConnectionString;
        await using var context = Context();
        await context.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private AuditDbContext Context()
    {
        var tenant = new TenantContext();
        tenant.Set(PersistenceTestDb.NewTenant());
        var options = (DbContextOptions<AuditDbContext>)AuditDbContext
            .UseAuditMySql(new DbContextOptionsBuilder<AuditDbContext>(), _connectionString)
            .Options;
        return new AuditDbContext(options, tenant, new FixedActor(null), TimeProvider.System);
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
            values.Add(reader.IsDBNull(0) ? string.Empty : reader.GetValue(0).ToString()!);
        }

        return values;
    }

    private Task<List<string>> PartitionsAsync() => ColumnAsync(
        "SELECT partition_name FROM information_schema.partitions "
        + "WHERE table_schema = DATABASE() AND table_name = 'audit_log' ORDER BY partition_ordinal_position");

    private async Task InsertAtAsync(string occurredAt)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var insert = new MySqlCommand(
            "INSERT INTO audit_log (id, tenant_id, occurred_at, entity_type, record_id, root_type, root_id, action, changes, actor_type) "
            + "VALUES (UUID_TO_BIN(UUID()), UUID_TO_BIN(UUID()), @at, 'thing', UUID_TO_BIN(UUID()), 'thing', UUID_TO_BIN(UUID()), 1, '[]', 1)",
            connection);
        insert.Parameters.AddWithValue("@at", occurredAt);
        await insert.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Migrations_CreateAMonthlyPartitionedTableWithACatchAll()
    {
        var partitions = await PartitionsAsync();

        partitions.Should().HaveCount(15);
        partitions[0].Should().Be("p202611");
        partitions[^2].Should().Be("p202712");
        partitions[^1].Should().Be("p_future");
    }

    [Theory]
    [InlineData("2026-11-30 23:59:59.999999", "p202611")]
    [InlineData("2026-12-01 00:00:00", "p202612")]
    [InlineData("2031-06-15 12:00:00", "p_future")]
    public async Task Insert_LandsInThePartitionOfItsUtcMonth(string occurredAt, string partition)
    {
        await InsertAtAsync(occurredAt);

        var rows = await ColumnAsync($"SELECT COUNT(*) FROM audit_log PARTITION ({partition})");

        rows.Should().Equal("1");
    }

    [Fact]
    public async Task Migrations_AppliedTwice_AreIdempotent()
    {
        await using var context = Context();

        await context.Invoking(c => c.Database.MigrateAsync()).Should().NotThrowAsync();
    }

    [Fact]
    public async Task Model_MatchesTheLatestMigration_AndCanBeQueried()
    {
        await using var context = Context();

        context.Database.HasPendingModelChanges().Should().BeFalse(
            "a model change needs `dotnet ef migrations add`; the snapshot is out of date");
        (await context.Entries.AsNoTracking().ToListAsync()).Should().BeEmpty();
    }

    [Theory]
    [InlineData("ix_audit_log_tenant_root", "tenant_id,root_type,root_id,occurred_at,id")]
    [InlineData("ix_audit_log_tenant_record", "tenant_id,entity_type,record_id,occurred_at,id")]
    public async Task HistoryIndexes_LeadWithTenantId(string index, string expected)
    {
        var columns = await ColumnAsync(
            "SELECT column_name FROM information_schema.statistics "
            + $"WHERE table_schema = DATABASE() AND table_name = 'audit_log' AND index_name = '{index}' ORDER BY seq_in_index");

        string.Join(',', columns).Should().Be(expected);
    }
}
