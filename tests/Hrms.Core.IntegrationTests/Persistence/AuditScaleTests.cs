using System.Diagnostics;
using System.Net;
using FluentAssertions;
using Hrms.Contracts;
using Hrms.Core.IntegrationTests.Employee;
using Hrms.Identity.Domain.Users;
using MySqlConnector;
using Xunit.Abstractions;

namespace Hrms.Core.IntegrationTests.Persistence;

/// <summary>
/// CLAUDE.md asks for reads under 300 ms at p95. This measures one employee's history in a 20,000-employee tenant whose log holds 500,000
/// other entries spread over thirteen monthly partitions, for a tenant-wide caller and for a manager whose reporting chain is a fifth of the
/// company (each permission the history needs is checked against that scope). As in the other scale tests the assertions are looser than
/// the target; the real numbers are in the test output.
/// </summary>
[Collection(MySqlTestGroup.Name)]
public sealed class AuditScaleTests(EmployeeApiFixture fixture, ITestOutputHelper output)
    : EmployeeApiTestBase(fixture), IClassFixture<EmployeeApiFixture>
{
    private const int OtherEntries = 500_000;
    private const int EntriesOfTheEmployee = 2_000;
    private const double GenerousP95Milliseconds = 1_000;

    private static readonly string[] Everything = [Permissions.Audit.LogView, .. Permissions.Employee.ReadAll];

    private sealed record SeededHistory(Guid Employee);

    private async Task<SeededHistory> SeedHistoryAsync()
    {
        var company = await ScaleCompany.SeedAsync(Fixture);
        return await Fixture.SharedAsync(async () =>
        {
            var admin = new MySqlConnectionStringBuilder(ConnectionString) { UserID = "root" };
            await using var connection = new MySqlConnection(admin.ConnectionString);
            await connection.OpenAsync();

            async Task RunAsync(string sql)
            {
                await using var command = new MySqlCommand(sql, connection) { CommandTimeout = 600 };
                command.Parameters.AddWithValue("@tenant", Acme.Tenant.TenantId.Value.ToString());
                command.Parameters.AddWithValue("@employee", company.ReportOfTopManager.ToString());
                await command.ExecuteNonQueryAsync();
            }

            // Entries are spread evenly from December 2026 to the end of 2027. record_id repeats within a root, as it does for real records.
            static string Insert(string rootId, int rows) => $"""
                INSERT INTO audit_log (id, tenant_id, occurred_at, entity_type, record_id, root_type, root_id, action, changes, actor_type)
                SELECT UUID_TO_BIN(UUID()), UUID_TO_BIN(@tenant),
                       TIMESTAMPADD(SECOND, MOD(n * 7919, 34000000), '2026-12-01'),
                       ELT(1 + MOD(n, 3), 'employee', 'employee_job', 'employee_personal'),
                       {rootId}, 'employee', {rootId}, 2,
                       '[{"{"}"field":"noticePeriodDays","old":30,"new":60,"sensitive":false{"}"}]', 1
                FROM (WITH RECURSIVE s AS (SELECT 1 AS n UNION ALL SELECT n + 1 FROM s WHERE n < {rows}) SELECT n FROM s) numbers
                """;

            await RunAsync("SET SESSION cte_max_recursion_depth = 1000000");

            // The other entries belong to as many different roots as the company has employees.
            await RunAsync(Insert($"UNHEX(MD5(CONCAT('other', MOD(n, {ScaleCompany.Employees}))))", OtherEntries));
            await RunAsync(Insert("UUID_TO_BIN(@employee)", EntriesOfTheEmployee));
            await RunAsync("ANALYZE TABLE audit_log");

            return new SeededHistory(company.ReportOfTopManager);
        });
    }

    private async Task MeasureAsync(string what, string path, string token, int runs = 20)
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
        var p95 = times[(int)Math.Ceiling(times.Length * 0.95) - 1];
        output.WriteLine($"{what}: median {times[times.Length / 2]:F0} ms, p95 {p95:F0} ms (target 300 ms, assertion {GenerousP95Milliseconds:F0} ms)");
        p95.Should().BeLessThan(GenerousP95Milliseconds, what);
    }

    private async Task<long> TotalAsync(string path, string token)
    {
        using var response = await GetAsync(path, token);
        var body = await JsonAsync(response);
        response.StatusCode.Should().Be(HttpStatusCode.OK, body.ToString());
        return body.GetProperty("total").GetInt64();
    }

    [Fact]
    public async Task History_InALargeLog_IsFastForATenantWideCaller()
    {
        var seeded = await SeedHistoryAsync();
        var (token, _) = await Host.TokenWithScopeAsync(
            Acme.Tenant.TenantId, "ACME", EmployeeApiFixture.Password, ScopeType.Tenant, null, null, Everything);
        var path = $"/audit/employee/{seeded.Employee}";

        (await TotalAsync(path, token)).Should().Be(EntriesOfTheEmployee);
        await MeasureAsync("tenant-wide, first page", path, token);
        await MeasureAsync("tenant-wide, last page", $"{path}?pageSize=50&page={EntriesOfTheEmployee / 50}", token);
        await MeasureAsync("tenant-wide, one month", $"{path}?from=2027-03-01T00:00:00Z&to=2027-04-01T00:00:00Z", token);
    }

    [Fact]
    public async Task History_InALargeLog_IsMeasuredForAManagerWhoseChainIsAFifthOfTheCompany()
    {
        var company = await ScaleCompany.SeedAsync(Fixture);
        var seeded = await SeedHistoryAsync();
        var (token, _) = await Host.TokenWithScopeAsync(
            Acme.Tenant.TenantId, "ACME", EmployeeApiFixture.Password, ScopeType.AllReports, null, company.TopManager, Everything);
        var path = $"/audit/employee/{seeded.Employee}";

        (await TotalAsync(path, token)).Should().Be(EntriesOfTheEmployee);
        await MeasureAsync("manager-chain scope, first page", path, token, runs: 10);
    }
}
