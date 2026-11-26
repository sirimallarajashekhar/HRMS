using System.Net;
using System.Text.Json;
using FluentAssertions;
using Hrms.Contracts;
using Hrms.Contracts.Workflow;
using Hrms.Core.IntegrationTests.Employee;
using Hrms.Core.IntegrationTests.Identity;
using Hrms.Identity.Domain.Users;
using Hrms.Workflow.Domain;
using Hrms.Workflow.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hrms.Core.IntegrationTests.Workflow;

/// <summary>An employee with a user account of their own, signed in.</summary>
public sealed record Person(Guid EmployeeId, Guid UserId, string Token);

/// <summary>Three levels of one reporting line: the worker reports to the lead, who reports to the head.</summary>
public sealed record Line(Person Worker, Person Lead, Person Head);

/// <summary>
/// Drives the workflow engine the way the product does: definitions and decisions through the API, starts through the module
/// contract, deadlines and delivery by running the worker's own pass. Every test makes its own people, so delegations or disabled
/// accounts in one test cannot affect another. Tests of one class run one after another, so publishing a new version is safe.
/// </summary>
public abstract class WorkflowApiTestBase(EmployeeApiFixture fixture) : EmployeeApiTestBase(fixture)
{
    private static readonly string[] AdminPermissions =
    [
        Permissions.Workflow.DefinitionView, Permissions.Workflow.DefinitionManage, Permissions.Workflow.InstanceView,
        Permissions.Workflow.InstanceManage, Permissions.Workflow.DelegationManage,
    ];

    private sealed record AdminTokens(string Acme, Guid AcmeUserId, string Globex);

    private Task<AdminTokens> AdminsAsync() => Fixture.SharedAsync(async () =>
    {
        var acme = await Host.TokenWithScopeAsync(Acme.Tenant.TenantId, Acme.Code, EmployeeApiFixture.Password, ScopeType.Tenant, null, null, AdminPermissions);
        var globex = await Host.TokenWithScopeAsync(Globex.Tenant.TenantId, Globex.Code, EmployeeApiFixture.Password, ScopeType.Tenant, null, null, AdminPermissions);
        return new AdminTokens(acme.Token, acme.UserId, globex.Token);
    });

    protected async Task<string> AdminAsync() => (await AdminsAsync()).Acme;

    protected async Task<Guid> AdminUserIdAsync() => (await AdminsAsync()).AcmeUserId;

    protected async Task<string> GlobexAdminAsync() => (await AdminsAsync()).Globex;

    protected async Task<Person> PersonAsync(Person? manager = null, TenantSetup? tenant = null, Guid? unit = null, Guid? location = null)
    {
        var s = tenant ?? Acme;
        var employee = Id(await CreateEmployeeAsync(s, CreateBody(s, manager: manager?.EmployeeId, unit: unit, location: location)));
        var (token, userId) = await Host.TokenWithScopeAsync(s.Tenant.TenantId, s.Code, EmployeeApiFixture.Password, ScopeType.Self, null, employee);
        return new Person(employee, userId, token);
    }

    protected async Task<Line> LineAsync()
    {
        var head = await PersonAsync();
        var lead = await PersonAsync(head);
        return new Line(await PersonAsync(lead), lead, head);
    }

    /// <summary>A user who is not an employee, with permissions and a scope.</summary>
    protected async Task<(string Token, Guid UserId)> UserAsync(ScopeType scope = ScopeType.Self, Guid? scopeValue = null, params string[] permissions) =>
        await Host.TokenWithScopeAsync(Acme.Tenant.TenantId, Acme.Code, EmployeeApiFixture.Password, scope, scopeValue, null, permissions);

    protected async Task<string> RoleCodeOfAsync(Guid userId)
    {
        await using var db = IdentityTestDb.Context(ConnectionString, Acme.Tenant.TenantId);
        return await (from grant in db.UserRoleScopes
                      join role in db.Roles on grant.RoleId equals role.Id
                      where grant.UserId == userId
                      select role.Code).SingleAsync();
    }

    protected Task DisableAsync(Guid userId) => Host.UpdateUserAsync(Acme.Tenant.TenantId, userId, u => u.Disable());

    // --- definitions -----------------------------------------------------------------------------------------------------

    protected static object Manager(int level = 1) => new { type = "managerLevel", level };

    protected static object Named(Guid userId) => new { type = "namedUser", userId };

    protected static object Stage(
        object approver,
        string name = "Approval",
        string mode = "any",
        object? condition = null,
        object? fallback = null,
        int? slaHours = null,
        string? onTimeout = null,
        object? escalation = null) => new { name, approver, mode, condition, fallback, slaHours, onTimeout, escalation };

    protected static object DaysOver(int days) => new { field = "days", op = "gt", value = days };

    /// <summary>Publishes a new version with these stages, effective today.</summary>
    protected async Task<Guid> PublishAsync(string key, params object[] stages)
    {
        var admin = await AdminAsync();
        using var created = await PostAsync("/workflow/definitions", new { workflowKey = key, name = "Test flow", stages }, admin);
        var draft = await JsonAsync(created);
        created.StatusCode.Should().Be(HttpStatusCode.Created, draft.ToString());

        using var published = await PostAsync($"/workflow/definitions/{Id(draft)}/publish", new { rowVersion = Version(draft) }, admin);
        published.StatusCode.Should().Be(HttpStatusCode.OK, await published.Content.ReadAsStringAsync());
        return Id(draft);
    }

    // --- the module's side -----------------------------------------------------------------------------------------------

    protected static Dictionary<string, object?> Days(decimal days) => new() { ["days"] = days };

    protected async Task<WorkflowSubmission> SubmitAsync(
        Person subject,
        Guid? requester = null,
        Dictionary<string, object?>? fields = null,
        string key = TestWorkflows.Leave,
        Guid? entityId = null)
    {
        await using var scope = Host.TenantScope(Acme.Tenant.TenantId);
        return await scope.ServiceProvider.GetRequiredService<IWorkflowService>().StartAsync(
            new StartWorkflow(key, entityId ?? Guid.CreateVersion7(), subject.EmployeeId, requester ?? subject.UserId, fields ?? Days(2)),
            CancellationToken.None);
    }

    /// <summary>Starts a workflow that is expected to be accepted; returns the instance id and the request id.</summary>
    protected async Task<(Guid Instance, Guid Entity)> StartAsync(
        Person subject, Guid? requester = null, decimal days = 2, string key = TestWorkflows.Leave)
    {
        var entity = Guid.CreateVersion7();
        var submission = await SubmitAsync(subject, requester, Days(days), key, entity);
        submission.IsSuccess.Should().BeTrue(submission.Failure?.Message ?? string.Empty);
        return (submission.Workflow!.InstanceId, entity);
    }

    protected async Task<bool> CanDecideAsync(Guid userId, Guid entityId)
    {
        await using var scope = Host.TenantScope(Acme.Tenant.TenantId);
        return await scope.ServiceProvider.GetRequiredService<IWorkflowService>()
            .CanDecideAsync(userId, TestWorkflows.Leave, entityId, CancellationToken.None);
    }

    protected DecisionRecorder Decisions => Host.Service<DecisionRecorder>();

    protected Task<(int Overdue, int Delivered)> ProcessAsync() =>
        Host.Service<WorkflowBackgroundService>().ProcessTenantAsync(Acme.Tenant.TenantId.Value, CancellationToken.None);

    // The test host's clock stands still unless a test moves it, so "past" means past for that clock, not the machine's.
    protected DateTime HostNow => Host.Clock.GetUtcNow().UtcDateTime;

    /// <summary>Moves the deadlines of a workflow's waiting tasks into the past.</summary>
    protected async Task MakeOverdueAsync(Guid instanceId)
    {
        await using var db = WorkflowTestDb.Context(ConnectionString, Acme.Tenant.TenantId);
        var tasks = await db.Tasks.Where(t => t.InstanceId == instanceId && t.Status == WorkflowTaskStatus.Pending).ToListAsync();
        tasks.Should().NotBeEmpty();
        foreach (var task in tasks)
        {
            db.Entry(task).Property(t => t.DueAt).CurrentValue = HostNow.AddMinutes(-5);
        }

        await db.SaveChangesAsync();
    }

    // --- the approver's side ---------------------------------------------------------------------------------------------

    protected async Task<JsonElement> InstanceAsync(Guid id, string? token = null)
    {
        using var response = await GetAsync($"/workflow/instances/{id}", token ?? await AdminAsync());
        var json = await JsonAsync(response);
        response.StatusCode.Should().Be(HttpStatusCode.OK, json.ToString());
        return json;
    }

    protected async Task<string> StatusAsync(Guid instanceId) =>
        (await InstanceAsync(instanceId)).GetProperty("instance").GetProperty("status").GetString()!;

    protected async Task<IReadOnlyList<JsonElement>> InboxAsync(string token, string query = "")
    {
        using var response = await GetAsync($"/workflow/tasks?pageSize=200{query}", token);
        var json = await JsonAsync(response);
        response.StatusCode.Should().Be(HttpStatusCode.OK, json.ToString());
        return json.GetProperty("items").EnumerateArray().ToList();
    }

    /// <summary>The id of the task for this workflow in the user's inbox, or null.</summary>
    protected async Task<Guid?> InboxTaskAsync(string token, Guid instanceId)
    {
        var items = await InboxAsync(token);
        var match = items.Where(t => t.GetProperty("instanceId").GetGuid() == instanceId).ToList();
        return match.Count == 0 ? null : Id(match.Single());
    }

    protected async Task<Guid> RequireTaskAsync(string token, Guid instanceId)
    {
        var task = await InboxTaskAsync(token, instanceId);
        task.Should().NotBeNull("the task should be waiting in this inbox");
        return task!.Value;
    }

    protected Task<HttpResponseMessage> ActAsync(string token, Guid taskId, string action, string? comment = null) =>
        PostAsync($"/workflow/tasks/{taskId}/actions", new { action, comment }, token);

    protected async Task DecideAsync(Person approver, Guid instanceId, string action = "approve", string? comment = null)
    {
        var task = await RequireTaskAsync(approver.Token, instanceId);
        using var response = await ActAsync(approver.Token, task, action, comment);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    protected async Task<HttpResponseMessage> DelegateAsync(string token, Guid delegateUserId, Guid? delegator = null, string? key = null) =>
        await PostAsync(
            "/workflow/delegations",
            new { delegatorUserId = delegator, delegateUserId, workflowKey = key, startsOn = Iso(Today), endsOn = Iso(Today.AddDays(7)) },
            token);

    protected static IReadOnlyList<string> Actions(JsonElement instance) =>
        instance.GetProperty("history").EnumerateArray().Select(h => h.GetProperty("action").GetString()!).ToList();

    protected static IReadOnlyList<(Guid Assignee, string Status)> Tasks(JsonElement instance) =>
        instance.GetProperty("tasks").EnumerateArray()
            .Select(t => (t.GetProperty("assigneeUserId").GetGuid(), t.GetProperty("status").GetString()!))
            .ToList();

    protected static async Task ExpectAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var json = await JsonAsync(response);
        response.StatusCode.Should().Be(status, json.ToString());
        ErrorCode(json).Should().Be(code);
        response.Dispose();
    }
}
