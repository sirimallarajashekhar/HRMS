using System.Diagnostics;
using System.Net;
using FluentAssertions;
using Hrms.Contracts;
using Hrms.Core.IntegrationTests.Persistence;
using Hrms.Identity.Domain.Users;
using MySqlConnector;
using Xunit.Abstractions;

namespace Hrms.Core.IntegrationTests.Employee;

/// <summary>
/// CLAUDE.md asks us to know the numbers: a tenant of 20,000 employees, list reads under 300 ms at p95. This seeds 20,000 employees in a
/// reporting tree (fan-out 5, so about six levels deep) straight into the tables, then measures the list, the search, the manager-chain scope
/// (a recursive query) and a scope-limited list. The assertions are deliberately looser than the target (shared CI machines are noisy); the
/// real numbers are written to the test output so a regression in the order of magnitude is visible.
/// </summary>
[Collection(MySqlTestGroup.Name)]
public class EmployeeScaleTests(EmployeeApiFixture fixture, ITestOutputHelper output) : EmployeeApiTestBase(fixture), IClassFixture<EmployeeApiFixture>
{
    private const int Employees = 20_000;
    private const int FanOut = 5;
    private const double GenerousP95Milliseconds = 1_000;

    private sealed record Seeded(Guid TopManager, string TopManagerCode, string SearchPrefix);

    private async Task<Seeded> SeedAsync() => await Fixture.SharedAsync(async () =>
    {
        var tenant = Acme.Tenant.TenantId.Value.ToString();
        var admin = new MySqlConnectionStringBuilder(ConnectionString) { UserID = "root" };
        await using var connection = new MySqlConnection(admin.ConnectionString);
        await connection.OpenAsync();

        async Task RunAsync(string sql)
        {
            await using var command = new MySqlCommand(sql, connection) { CommandTimeout = 300 };
            command.Parameters.AddWithValue("@tenant", tenant);
            command.Parameters.AddWithValue("@le", Acme.LegalEntityId.ToString());
            command.Parameters.AddWithValue("@loc", Acme.LocationId.ToString());
            command.Parameters.AddWithValue("@unit", Acme.DepartmentAId.ToString());
            command.Parameters.AddWithValue("@des", Acme.DesignationId.ToString());
            command.Parameters.AddWithValue("@type", Acme.EmployeeTypeId.ToString());
            await command.ExecuteNonQueryAsync();
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

        // Employee number 2 is a child of the root and manages about a fifth of the company.
        await using var read = new MySqlCommand("SELECT BIN_TO_UUID(id) FROM scale_seed WHERE n = 2", connection);
        var id = Guid.Parse((string)(await read.ExecuteScalarAsync())!);
        await RunAsync("DROP TABLE scale_seed");

        // A bulk insert leaves the optimizer statistics of the empty tables in place until MySQL recalculates them in the background; do it now,
        // as it would have happened in a real tenant that grew over time.
        await RunAsync("ANALYZE TABLE employee, employee_job, employee_personal");
        return new Seeded(id, "SC000002", "Last0100");
    });

    private async Task<double[]> MeasureAsync(string path, string token, int runs = 20)
    {
        (await GetAsync(path, token)).Dispose(); // warm up the connection pool and the query plan cache
        var times = new double[runs];
        for (var i = 0; i < runs; i++)
        {
            var watch = Stopwatch.StartNew();
            using var response = await GetAsync(path, token);
            watch.Stop();
            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            times[i] = watch.Elapsed.TotalMilliseconds;
        }

        Array.Sort(times);
        return times;
    }

    private double P95(string what, double[] sorted)
    {
        var p95 = sorted[(int)Math.Ceiling(sorted.Length * 0.95) - 1];
        output.WriteLine($"{what}: median {sorted[sorted.Length / 2]:F0} ms, p95 {p95:F0} ms (target 300 ms, assertion {GenerousP95Milliseconds:F0} ms)");
        return p95;
    }

    [Fact]
    public async Task TheListOfATwentyThousandPersonTenant_IsFastForTheCommonQueries()
    {
        var seeded = await SeedAsync();

        P95("first page", await MeasureAsync("/employees?pageSize=50", Acme.Viewer)).Should().BeLessThan(GenerousP95Milliseconds);
        P95("last page", await MeasureAsync("/employees?pageSize=50&page=400", Acme.Viewer)).Should().BeLessThan(GenerousP95Milliseconds);
        P95("search by code prefix", await MeasureAsync($"/employees?search={seeded.TopManagerCode}", Acme.Viewer)).Should().BeLessThan(GenerousP95Milliseconds);
        P95("search by name prefix", await MeasureAsync($"/employees?search={seeded.SearchPrefix}", Acme.Viewer)).Should().BeLessThan(GenerousP95Milliseconds);
        P95("filter by manager", await MeasureAsync($"/employees?managerId={seeded.TopManager}", Acme.Viewer)).Should().BeLessThan(GenerousP95Milliseconds);
        P95("filter by org unit with sub-units", await MeasureAsync($"/employees?orgUnitId={Acme.DepartmentAId}&pageSize=50", Acme.Viewer)).Should().BeLessThan(GenerousP95Milliseconds);

        using var page = await GetAsync("/employees?pageSize=1", Acme.Viewer);
        (await JsonAsync(page)).GetProperty("total").GetInt64().Should().BeGreaterThanOrEqualTo(Employees);
    }

    [Fact]
    public async Task AManagerWithAThousandsStrongTree_ListsTheirReportsQuickly_ThroughTheChainScope()
    {
        var seeded = await SeedAsync();
        var (token, _) = await Host.TokenWithScopeAsync(
            Acme.Tenant.TenantId, "ACME", EmployeeApiFixture.Password, ScopeType.AllReports, null, seeded.TopManager, Permissions.Employee.EmployeeView);

        var times = await MeasureAsync("/employees?pageSize=50", token, runs: 10);

        P95("all-reports scope (a fifth of the company), first page", times).Should().BeLessThan(GenerousP95Milliseconds * 2);
        using var response = await GetAsync("/employees?pageSize=1", token);
        var total = (await JsonAsync(response)).GetProperty("total").GetInt64();
        output.WriteLine($"the manager's chain holds {total} employees");
        total.Should().BeInRange(Employees / FanOut - 1_000, Employees / FanOut + 1_000);
    }

    [Fact]
    public async Task ATopManagerWhoseChainIsBeyondTheCap_IsToldToUseATenantWideScope()
    {
        await SeedAsync();
        // Employee number 1 is the root: the whole company reports to them, far more than the scope service will materialise.
        await using var db = EmployeeTestDb.Context(ConnectionString, Acme.Tenant.TenantId);
        var root = db.Employees.Where(e => e.Code == "SC000001").Select(e => e.Id).Single();
        var (token, _) = await Host.TokenWithScopeAsync(
            Acme.Tenant.TenantId, "ACME", EmployeeApiFixture.Password, ScopeType.AllReports, null, root, Permissions.Employee.EmployeeView);

        using var response = await GetAsync("/employees", token);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        ErrorCode(await JsonAsync(response)).Should().Be("employee.scope.too_large");
    }
}
