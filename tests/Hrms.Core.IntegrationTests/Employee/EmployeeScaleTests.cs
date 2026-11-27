using System.Diagnostics;
using System.Net;
using FluentAssertions;
using Hrms.Contracts;
using Hrms.Core.IntegrationTests.Persistence;
using Hrms.Identity.Domain.Users;
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
    private const int Employees = ScaleCompany.Employees;
    private const int FanOut = ScaleCompany.FanOut;
    private const double GenerousP95Milliseconds = 1_000;

    private Task<ScaleCompany.Seeded> SeedAsync() => ScaleCompany.SeedAsync(Fixture);

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
