using FluentAssertions;
using Hrms.Core.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;

namespace Hrms.Core.IntegrationTests.Identity;

[Collection(MySqlTestGroup.Name)]
public class IdentitySchemaTests(MySqlFixture mysql) : IAsyncLifetime
{
    private string _connectionString = string.Empty;

    public async Task InitializeAsync() => _connectionString = await IdentityTestDb.CreateMigratedAsync(mysql);

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<List<string>> QueryAsync(string sql)
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

    private Task<List<string>> IndexColumnsAsync(string table, string index) => QueryAsync(
        "SELECT column_name FROM information_schema.statistics "
        + $"WHERE table_schema = DATABASE() AND table_name = '{table}' AND index_name = '{index}' ORDER BY seq_in_index");

    [Fact]
    public async Task Migrations_ApplyToAnEmptyDatabase_AndCreateTheIdentityTables()
    {
        var tables = await QueryAsync("SELECT table_name FROM information_schema.tables WHERE table_schema = DATABASE()");

        tables.Should().Contain(["user_account", "role", "role_permission", "user_role_scope", "tenant_security_policy", "permission", "ef_migrations_history_identity"]);
    }

    [Fact]
    public async Task Migrations_AppliedTwice_AreIdempotent()
    {
        await using var context = IdentityTestDb.Context(_connectionString, tenant: null);

        var act = () => context.Database.MigrateAsync();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Model_MatchesTheLatestMigration_SoNoMigrationIsMissing()
    {
        await using var context = IdentityTestDb.Context(_connectionString, tenant: null);

        context.Database.HasPendingModelChanges().Should().BeFalse("a model change needs `dotnet ef migrations add`");
    }

    [Theory]
    [InlineData("user_account", "ux_user_account_tenant_email", "tenant_id,active_email")]
    [InlineData("role", "ux_role_tenant_code", "tenant_id,active_code")]
    [InlineData("role_permission", "ux_role_permission_tenant_role_permission", "tenant_id,role_id,permission_code,active_flag")]
    [InlineData("user_role_scope", "ux_user_role_scope_tenant_grant", "tenant_id,user_id,role_id,scope_type,scope_value,active_flag")]
    [InlineData("tenant_security_policy", "ix_tenant_security_policy_tenant_effective_from", "tenant_id,effective_from")]
    public async Task Indexes_LeadWithTenantId(string table, string index, string expectedColumns) =>
        (await IndexColumnsAsync(table, index)).Should().Equal(expectedColumns.Split(','));

    [Theory]
    [InlineData("user_account", "ux_user_account_tenant_email")]
    [InlineData("role", "ux_role_tenant_code")]
    [InlineData("role_permission", "ux_role_permission_tenant_role_permission")]
    [InlineData("user_role_scope", "ux_user_role_scope_tenant_grant")]
    public async Task TheTenantUniqueIndexes_AreUnique(string table, string index)
    {
        var nonUnique = await QueryAsync(
            "SELECT DISTINCT non_unique FROM information_schema.statistics "
            + $"WHERE table_schema = DATABASE() AND table_name = '{table}' AND index_name = '{index}'");

        nonUnique.Should().Equal("0");
    }

    [Fact]
    public async Task SecurityPolicy_CheckConstraints_RejectOutOfRangeValues()
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var insert = new MySqlCommand(
            "INSERT INTO tenant_security_policy (id, tenant_id, effective_from, min_password_length, require_uppercase, require_lowercase, "
            + "require_digit, require_symbol, max_failed_attempts, lockout_minutes, created_at, is_deleted, row_version) "
            + "VALUES (UUID_TO_BIN(UUID()), UUID_TO_BIN(UUID()), '2026-01-01', 4, 0, 0, 0, 0, 5, 15, UTC_TIMESTAMP(6), 0, 1)",
            connection);

        var act = () => insert.ExecuteNonQueryAsync();

        (await act.Should().ThrowAsync<MySqlException>()).Which.Message.Should().Contain("ck_tenant_security_policy_min_length");
    }

    [Fact]
    public async Task RoleGrant_ForAPermissionNotInTheCatalogueTable_IsRefusedByTheForeignKey()
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var insertRole = new MySqlCommand(
            "INSERT INTO role (id, tenant_id, code, name, is_system, requires_mfa, created_at, is_deleted, row_version) "
            + "VALUES (UNHEX('11111111111111111111111111111111'), UNHEX('22222222222222222222222222222222'), 'x_role', 'X', 0, 0, UTC_TIMESTAMP(6), 0, 1)",
            connection);
        await insertRole.ExecuteNonQueryAsync();
        await using var insertGrant = new MySqlCommand(
            "INSERT INTO role_permission (id, tenant_id, role_id, permission_code, created_at, is_deleted, row_version) "
            + "VALUES (UUID_TO_BIN(UUID()), UNHEX('22222222222222222222222222222222'), UNHEX('11111111111111111111111111111111'), 'no.such.permission', UTC_TIMESTAMP(6), 0, 1)",
            connection);

        var act = () => insertGrant.ExecuteNonQueryAsync();

        (await act.Should().ThrowAsync<MySqlException>()).Which.Message.Should().Contain("foreign key");
    }
}
