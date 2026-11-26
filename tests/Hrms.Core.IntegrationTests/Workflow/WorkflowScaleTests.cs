using System.Diagnostics;
using FluentAssertions;
using Hrms.Core.IntegrationTests.Employee;
using Hrms.Core.IntegrationTests.Persistence;
using MySqlConnector;
using Xunit.Abstractions;

namespace Hrms.Core.IntegrationTests.Workflow;

/// <summary>
/// CLAUDE.md asks us to know the numbers: reads under 300 ms at p95. This adds 100,000 pending tasks (one per workflow) straight to
/// the tables: 99,000 spread over 5,000 other approvers and 1,000 for one busy approver, who also has a delegate. It then times the
/// inbox, and one deadline pass over 500 overdue tasks. Assertions are looser than the target because shared CI machines are noisy;
/// the measured numbers are written to the test output.
/// </summary>
[Collection(MySqlTestGroup.Name)]
public class WorkflowScaleTests(EmployeeApiFixture fixture, ITestOutputHelper output)
    : WorkflowApiTestBase(fixture), IClassFixture<EmployeeApiFixture>
{
    private const int Bulk = 100_000;
    private const int Busy = 1_000;
    private const int Overdue = 500;
    private const double GenerousP95Milliseconds = 1_000;

    private sealed record Seeded(Person Approver, Person Deputy);

    private Task<Seeded> SeedAsync() => Fixture.SharedAsync(async () =>
    {
        await PublishAsync(TestWorkflows.Leave, Stage(Manager(), slaHours: 24, onTimeout: "remind"));
        var line = await LineAsync();
        var deputy = await PersonAsync();
        (await DelegateAsync(line.Lead.Token, deputy.UserId)).Dispose();
        var (template, _) = await StartAsync(line.Worker);

        var admin = new MySqlConnectionStringBuilder(ConnectionString) { UserID = "root" };
        await using var connection = new MySqlConnection(admin.ConnectionString);
        await connection.OpenAsync();

        async Task RunAsync(string sql)
        {
            await using var command = new MySqlCommand(sql, connection) { CommandTimeout = 600 };
            command.Parameters.AddWithValue("@tenant", Acme.Tenant.TenantId.Value.ToString());
            command.Parameters.AddWithValue("@template", template.ToString());
            command.Parameters.AddWithValue("@approver", line.Lead.UserId.ToString());
            command.Parameters.AddWithValue("@dueLater", HostNow.AddDays(30));
            command.Parameters.AddWithValue("@duePast", HostNow.AddHours(-1));
            await command.ExecuteNonQueryAsync();
        }

        await RunAsync("SET SESSION cte_max_recursion_depth = 200000");
        await RunAsync("DROP TABLE IF EXISTS wf_scale_seed");
        await RunAsync("CREATE TABLE wf_scale_seed (n INT PRIMARY KEY, instance_id BINARY(16) NOT NULL, task_id BINARY(16) NOT NULL, other_user BINARY(16) NOT NULL)");
        await RunAsync($"""
            INSERT INTO wf_scale_seed
            SELECT n, UUID_TO_BIN(UUID()), UUID_TO_BIN(UUID()), UNHEX(MD5(CONCAT('approver', n % 5000)))
            FROM (WITH RECURSIVE s AS (SELECT 1 AS n UNION ALL SELECT n + 1 FROM s WHERE n < {Bulk}) SELECT n FROM s) numbers
            """);
        await RunAsync("""
            INSERT INTO workflow_instance (id, tenant_id, created_at, is_deleted, row_version, definition_id, definition_version, workflow_key,
                entity_type, entity_id, subject_employee_id, requester_user_id, status, current_stage, round, payload_json, action_count,
                event_count, start_fingerprint)
            SELECT s.instance_id, i.tenant_id, i.created_at, 0, 1, i.definition_id, i.definition_version, i.workflow_key,
                i.entity_type, s.instance_id, i.subject_employee_id, i.requester_user_id, 'Pending', 1, 1, i.payload_json, 0, 0, i.start_fingerprint
            FROM wf_scale_seed s JOIN workflow_instance i ON i.id = UUID_TO_BIN(@template) AND i.tenant_id = UUID_TO_BIN(@tenant)
            """);

        // One task in a hundred goes to the busy approver. The first 500 of everyone else's are overdue.
        await RunAsync($"""
            INSERT INTO workflow_task (id, tenant_id, created_at, is_deleted, row_version, instance_id, stage_order, round, assignee_user_id,
                origin, status, due_at, assignee_unavailable)
            SELECT task_id, UUID_TO_BIN(@tenant), TIMESTAMPADD(SECOND, n, '2026-01-01'), 0, 1, instance_id, 1, 1,
                IF(n % {Bulk / Busy} = 0, UUID_TO_BIN(@approver), other_user), 'Rule', 'Pending',
                IF(n % {Bulk / Busy} <> 0 AND n <= {Overdue + (Overdue / 99)}, @duePast, @dueLater), 0
            FROM wf_scale_seed
            """);
        await RunAsync("DROP TABLE wf_scale_seed");

        // As after any bulk load (see CLAUDE.md): refresh the optimizer statistics.
        await RunAsync("ANALYZE TABLE workflow_instance, workflow_task");
        return new Seeded(line.Lead, deputy);
    });

    private async Task<double> P95Async(string what, string path, string token, int runs = 20)
    {
        (await GetAsync(path, token)).Dispose(); // warm up the connection pool and the query plan cache
        var times = new double[runs];
        for (var i = 0; i < runs; i++)
        {
            var watch = Stopwatch.StartNew();
            using var response = await GetAsync(path, token);
            watch.Stop();
            response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync());
            times[i] = watch.Elapsed.TotalMilliseconds;
        }

        Array.Sort(times);
        var p95 = times[(int)Math.Ceiling(runs * 0.95) - 1];
        output.WriteLine($"{what}: median {times[runs / 2]:F0} ms, p95 {p95:F0} ms (target 300 ms, assertion {GenerousP95Milliseconds:F0} ms)");
        return p95;
    }

    [Fact]
    public async Task TheInbox_StaysFast_WithAHundredThousandWaitingTasks()
    {
        var seeded = await SeedAsync();
        var mine = await InboxAsync(seeded.Approver.Token);
        mine.Should().HaveCount(200, "the page is capped");

        (await P95Async("own inbox (about 1,000 of 100,000), first page", "/workflow/tasks?pageSize=50", seeded.Approver.Token))
            .Should().BeLessThan(GenerousP95Milliseconds);
        (await P95Async("own inbox, a late page", "/workflow/tasks?pageSize=50&page=20", seeded.Approver.Token))
            .Should().BeLessThan(GenerousP95Milliseconds);
        (await P95Async("delegate's inbox of those tasks, first page", "/workflow/tasks?pageSize=50", seeded.Deputy.Token))
            .Should().BeLessThan(GenerousP95Milliseconds);
        (await P95Async("one task", $"/workflow/tasks/{Id(mine[0])}", seeded.Approver.Token))
            .Should().BeLessThan(GenerousP95Milliseconds);
        (await P95Async("blocked instances list", "/workflow/instances?status=blocked", await AdminAsync()))
            .Should().BeLessThan(GenerousP95Milliseconds);
    }

    [Fact]
    public async Task ADeadlinePass_HandlesABatchOfOverdueTasks()
    {
        await SeedAsync();

        var watch = Stopwatch.StartNew();
        var (overdue, delivered) = await ProcessAsync();
        watch.Stop();

        output.WriteLine($"one pass: {overdue} overdue tasks reminded and {delivered} messages delivered in {watch.Elapsed.TotalSeconds:F1} s");
        overdue.Should().Be(Overdue);
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(120));
    }
}
