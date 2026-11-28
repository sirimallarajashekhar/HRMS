using MySqlConnector;

namespace Hrms.Core.IntegrationTests.Employee;

/// <summary>
/// 20,000 employees in ACME in a reporting tree (fan-out 5, so about six levels deep), written straight into the tables and shared by the
/// tests of a class. Employee SC000001 is the root. Rows seeded this way have no audit history.
/// </summary>
internal static class ScaleCompany
{
    public const int Employees = 20_000;
    public const int FanOut = 5;

    /// <param name="TopManager">SC000002, a child of the root who manages about a fifth of the company.</param>
    /// <param name="ReportOfTopManager">SC000007, who reports to <paramref name="TopManager"/>.</param>
    internal sealed record Seeded(Guid TopManager, string TopManagerCode, Guid ReportOfTopManager, string SearchPrefix);

    public static Task<Seeded> SeedAsync(EmployeeApiFixture fixture) => fixture.SharedAsync(() => CreateAsync(fixture));

    private static async Task<Seeded> CreateAsync(EmployeeApiFixture fixture)
    {
        var acme = fixture.Acme;
        var tenant = acme.Tenant.TenantId.Value.ToString();
        var admin = new MySqlConnectionStringBuilder(fixture.ConnectionString) { UserID = "root" };
        await using var connection = new MySqlConnection(admin.ConnectionString);
        await connection.OpenAsync();

        async Task RunAsync(string sql)
        {
            await using var command = new MySqlCommand(sql, connection) { CommandTimeout = 300 };
            command.Parameters.AddWithValue("@tenant", tenant);
            command.Parameters.AddWithValue("@le", acme.LegalEntityId.ToString());
            command.Parameters.AddWithValue("@loc", acme.LocationId.ToString());
            command.Parameters.AddWithValue("@unit", acme.DepartmentAId.ToString());
            command.Parameters.AddWithValue("@des", acme.DesignationId.ToString());
            command.Parameters.AddWithValue("@type", acme.EmployeeTypeId.ToString());
            await command.ExecuteNonQueryAsync();
        }

        async Task<Guid> IdOfAsync(int number)
        {
            await using var command = new MySqlCommand("SELECT BIN_TO_UUID(id) FROM scale_seed WHERE n = @n", connection);
            command.Parameters.AddWithValue("@n", number);
            return Guid.Parse((string)(await command.ExecuteScalarAsync())!);
        }

        await RunAsync("SET SESSION cte_max_recursion_depth = 100000");
        await RunAsync("DROP TABLE IF EXISTS scale_seed");
        await RunAsync("CREATE TABLE scale_seed (n INT PRIMARY KEY, id BINARY(16) NOT NULL, personal_id BINARY(16) NOT NULL, job_id BINARY(16) NOT NULL)");
        await RunAsync($"""
            INSERT INTO scale_seed
            SELECT n, UUID_TO_BIN(UUID()), UUID_TO_BIN(UUID()), UUID_TO_BIN(UUID())
            FROM (WITH RECURSIVE s AS (SELECT 1 AS n UNION ALL SELECT n + 1 FROM s WHERE n < {Employees}) SELECT n FROM s) numbers
            """);
        await RunAsync("""
            INSERT INTO employee (id, tenant_id, created_at, is_deleted, row_version, code, joining_date)
            SELECT id, UUID_TO_BIN(@tenant), UTC_TIMESTAMP(6), 0, 1, CONCAT('SC', LPAD(n, 6, '0')), '2020-01-01' FROM scale_seed
            """);
        await RunAsync("""
            INSERT INTO employee_personal (id, tenant_id, created_at, is_deleted, row_version, employee_id, first_name, last_name, date_of_birth, gender)
            SELECT personal_id, UUID_TO_BIN(@tenant), UTC_TIMESTAMP(6), 0, 1, id, CONCAT('First', n), CONCAT('Last', LPAD(n, 6, '0')), '1990-01-01', 1 FROM scale_seed
            """);
        await RunAsync($"""
            INSERT INTO employee_job (id, tenant_id, created_at, is_deleted, row_version, employee_id, legal_entity_id, location_id, org_unit_id, designation_id,
                                      employee_type_id, manager_id, notice_period_days, reason, event_published_at, effective_from)
            SELECT s.job_id, UUID_TO_BIN(@tenant), UTC_TIMESTAMP(6), 0, 1, s.id, UUID_TO_BIN(@le), UUID_TO_BIN(@loc), UUID_TO_BIN(@unit), UUID_TO_BIN(@des),
                   UUID_TO_BIN(@type), m.id, 30, 1, UTC_TIMESTAMP(6), '2020-01-01'
            FROM scale_seed s LEFT JOIN scale_seed m ON m.n = ((s.n - 2) DIV {FanOut}) + 1 AND s.n > 1
            """);

        // The children of number 2 are numbers 7 to 11.
        var topManager = await IdOfAsync(2);
        var report = await IdOfAsync(7);
        await RunAsync("DROP TABLE scale_seed");

        // A bulk insert leaves the optimizer statistics of the empty tables in place until MySQL recalculates them in the background; do it now,
        // as it would have happened in a real tenant that grew over time.
        await RunAsync("ANALYZE TABLE employee, employee_job, employee_personal");
        return new Seeded(topManager, "SC000002", report, "Last0100");
    }
}
