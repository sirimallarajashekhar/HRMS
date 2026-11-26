using FluentAssertions;
using Hrms.Core.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;

namespace Hrms.Core.IntegrationTests.Core;

[Collection(MySqlTestGroup.Name)]
public class CoreSchemaTests(MySqlFixture mysql) : IAsyncLifetime
{
    private string _connectionString = string.Empty;

    public async Task InitializeAsync() => _connectionString = await CoreTestDb.CreateMigratedAsync(mysql);

    public Task DisposeAsync() => Task.CompletedTask;

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

    [Fact]
    public async Task Migrations_ApplyToAnEmptyDatabase_AndCreateTheCoreTables()
    {
        var tables = await ColumnAsync("SELECT table_name FROM information_schema.tables WHERE table_schema = DATABASE()");

        tables.Should().Contain(["tenant", "legal_entity", "ef_migrations_history_core"]);
    }

    [Fact]
    public async Task Migrations_AppliedTwice_AreIdempotent()
    {
        await using var context = CoreTestDb.Context(_connectionString, tenant: null);

        var act = () => context.Database.MigrateAsync();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Model_MatchesTheLatestMigration_SoNoMigrationIsMissing()
    {
        await using var context = CoreTestDb.Context(_connectionString, tenant: null);

        context.Database.HasPendingModelChanges().Should().BeFalse(
            "a model change needs `dotnet ef migrations add`; the snapshot is out of date");
    }

    [Fact]
    public async Task LegalEntityUniqueIndex_LeadsWithTenantIdThenTheActiveCodeColumn()
    {
        var columns = await ColumnAsync(
            "SELECT column_name FROM information_schema.statistics "
            + "WHERE table_schema = DATABASE() AND table_name = 'legal_entity' AND index_name = 'ux_legal_entity_tenant_code' "
            + "ORDER BY seq_in_index");
        var nonUnique = await ColumnAsync(
            "SELECT DISTINCT non_unique FROM information_schema.statistics "
            + "WHERE table_schema = DATABASE() AND table_name = 'legal_entity' AND index_name = 'ux_legal_entity_tenant_code'");

        columns.Should().Equal("tenant_id", "active_code");
        nonUnique.Should().Equal("0");
    }

    [Fact]
    public async Task TenantTable_HasAUniqueCodeIndexAndBinarySixteenIds()
    {
        var unique = await ColumnAsync(
            "SELECT non_unique FROM information_schema.statistics "
            + "WHERE table_schema = DATABASE() AND table_name = 'tenant' AND index_name = 'ux_tenant_code'");
        var idType = await ColumnAsync(
            "SELECT column_type FROM information_schema.columns "
            + "WHERE table_schema = DATABASE() AND table_name = 'tenant' AND column_name = 'id'");

        unique.Should().Equal("0");
        idType.Should().Equal("binary(16)");
    }

    [Fact]
    public async Task DatabaseCheckConstraint_RejectsAFiscalMonthOutsideOneToTwelve()
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var insert = new MySqlCommand(
            "INSERT INTO legal_entity (id, tenant_id, code, name, country_code, currency_code, fiscal_year_start_month, "
            + "country_pack_code, default_language, time_zone_id, created_at, is_deleted, row_version) "
            + "VALUES (UUID_TO_BIN(UUID()), UUID_TO_BIN(UUID()), 'BAD', 'Bad', 'IN', 'INR', 13, 'IN', 'en-IN', 'Asia/Kolkata', "
            + "UTC_TIMESTAMP(6), 0, 1)",
            connection);

        var act = () => insert.ExecuteNonQueryAsync();

        (await act.Should().ThrowAsync<MySqlException>()).Which.Message.Should().Contain("ck_legal_entity_fiscal_year_start_month");
    }
}
