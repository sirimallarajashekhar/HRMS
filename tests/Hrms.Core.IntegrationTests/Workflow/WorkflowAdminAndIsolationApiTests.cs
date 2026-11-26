using System.Net;
using System.Text.Json;
using FluentAssertions;
using Hrms.Contracts;
using Hrms.Core.IntegrationTests.Employee;
using Hrms.Core.IntegrationTests.Persistence;
using Hrms.Identity.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace Hrms.Core.IntegrationTests.Workflow;

/// <summary>Definitions, permissions, data scope and tenant isolation for every workflow endpoint, and the schema.</summary>
[Collection(MySqlTestGroup.Name)]
public class WorkflowAdminAndIsolationApiTests(EmployeeApiFixture fixture) : WorkflowApiTestBase(fixture), IClassFixture<EmployeeApiFixture>
{
    private async Task<HttpResponseMessage> DraftAsync(params object[] stages) =>
        await PostAsync("/workflow/definitions", new { workflowKey = TestWorkflows.Leave, name = "Draft", stages }, await AdminAsync());

    [Fact]
    public async Task Model_MatchesTheLatestMigration_SoNoMigrationIsMissing()
    {
        await using var db = WorkflowTestDb.Context(ConnectionString, Acme.Tenant.TenantId);

        db.Database.HasPendingModelChanges().Should().BeFalse("a model change needs `dotnet ef migrations add`");
    }

    [Fact]
    public async Task Keys_ListWhatTheModulesDeclare()
    {
        using var response = await GetAsync("/workflow/keys", await AdminAsync());

        var keys = (await JsonAsync(response)).EnumerateArray().ToList();
        keys.Select(k => k.GetProperty("key").GetString()).Should().Equal(TestWorkflows.Auto, TestWorkflows.Leave);
        keys[1].GetProperty("allowAutoApprove").GetBoolean().Should().BeFalse();
    }

    public static TheoryData<string, object[]> BadStages => new()
    {
        { "workflow.definition.no_unconditional_stage", [Stage(Manager(), condition: DaysOver(1))] },
        { "workflow.definition.stage_count_invalid", [] },
        { "workflow.definition.approver_invalid", [Stage(new { type = "somebody" })] },
        { "workflow.definition.auto_approve_not_allowed", [Stage(Manager(), slaHours: 1, onTimeout: "approve")] },
        { "workflow.definition.reference_not_found", [Stage(new { type = "roleAtLocation", roleCode = "no_such_role" })] },
        { "workflow.definition.reference_not_found", [Stage(new { type = "namedUser", userId = Guid.NewGuid() })] },
        { "workflow.definition.reference_not_found", [Stage(new { type = "unitHead", orgUnitTypeId = Guid.NewGuid() })] },
        { "workflow.condition.invalid", [Stage(Manager()), Stage(Manager(2), condition: new { field = "salary", op = "gt", value = 1 })] },
        { "workflow.definition.mode_invalid", [Stage(Manager(), mode: "most")] },
        { "workflow.definition.timeout_invalid", [Stage(Manager(), slaHours: 1, onTimeout: "ignore")] },
    };

    [Theory]
    [MemberData(nameof(BadStages))]
    public async Task AnUnsafeOrBrokenDefinition_IsRefused(string code, object[] stages) =>
        await ExpectAsync(await DraftAsync(stages), HttpStatusCode.UnprocessableEntity, code);

    [Fact]
    public async Task ADraftIsEditable_APublishedVersionIsNot()
    {
        var admin = await AdminAsync();
        using var created = await DraftAsync(Stage(Manager(), "First"));
        var draft = await JsonAsync(created);
        created.StatusCode.Should().Be(HttpStatusCode.Created, draft.ToString());
        await ExpectAsync(await DraftAsync(Stage(Manager())), HttpStatusCode.Conflict, "workflow.definition.draft_exists");

        var change = new { name = "Changed", stages = new[] { Stage(Manager(2), "Second", condition: DaysOver(2)), Stage(Manager(), "Third") }, rowVersion = Version(draft) };
        using var updated = await PutAsync($"/workflow/definitions/{Id(draft)}", change, admin);
        var after = await JsonAsync(updated);
        updated.StatusCode.Should().Be(HttpStatusCode.OK, after.ToString());
        after.GetProperty("stages").EnumerateArray().Select(s => s.GetProperty("name").GetString()).Should().Equal("Second", "Third");
        after.GetProperty("stages")[0].GetProperty("condition").GetProperty("value").GetInt32().Should().Be(2);
        await ExpectAsync(await PutAsync($"/workflow/definitions/{Id(draft)}", change, admin), HttpStatusCode.Conflict, "workflow.definition.stale_version");

        await ExpectAsync(
            await PostAsync($"/workflow/definitions/{Id(draft)}/publish", new { effectiveFrom = Iso(Today.AddDays(-1)), rowVersion = Version(after) }, admin),
            HttpStatusCode.UnprocessableEntity, "workflow.definition.effective_from_past");
        using var published = await PostAsync($"/workflow/definitions/{Id(draft)}/publish", new { rowVersion = Version(after) }, admin);
        var live = await JsonAsync(published);
        live.GetProperty("status").GetString().Should().Be("published");

        await ExpectAsync(
            await PutAsync($"/workflow/definitions/{Id(draft)}", new { name = "Again", stages = new[] { Stage(Manager()) }, rowVersion = Version(live) }, admin),
            HttpStatusCode.Conflict, "workflow.definition.not_draft");
    }

    [Fact]
    public async Task AVersionThatStartsLater_DoesNotApplyYet()
    {
        var admin = await AdminAsync();
        var line = await LineAsync();
        await PublishAsync(TestWorkflows.Leave, Stage(Manager()));
        using var created = await DraftAsync(Stage(Manager(2)));
        var draft = await JsonAsync(created);
        using var published = await PostAsync($"/workflow/definitions/{Id(draft)}/publish", new { effectiveFrom = Iso(Today.AddDays(30)), rowVersion = Version(draft) }, admin);
        published.StatusCode.Should().Be(HttpStatusCode.OK, await published.Content.ReadAsStringAsync());

        var (instance, _) = await StartAsync(line.Worker);

        (await RequireTaskAsync(line.Lead.Token, instance)).Should().NotBeEmpty();

        // Leave no future version behind for the other tests: later publishes must start on or after it.
        await using var db = WorkflowTestDb.Context(ConnectionString, Acme.Tenant.TenantId);
        db.Stages.RemoveRange(db.Stages.Where(s => s.DefinitionId == Id(draft)));
        db.Definitions.RemoveRange(db.Definitions.Where(d => d.Id == Id(draft)));
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task AnOrgUnitTypeThatARuleNames_CannotBeDeleted()
    {
        using var created = await PostAsync("/org-unit-types", new { code = "GUILD", name = "Guild", level = 9 }, Acme.OrgAdmin);
        var type = Id(await JsonAsync(created));
        await PublishAsync(TestWorkflows.Leave, Stage(new { type = "unitHead", orgUnitTypeId = type }));

        using var deleted = await DeleteAsync($"/org-unit-types/{type}", Acme.OrgAdmin);

        deleted.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task AnotherTenant_SeesAndChangesNothing_ThroughAnyEndpoint()
    {
        await PublishAsync(TestWorkflows.Leave, Stage(Manager(), slaHours: 24, onTimeout: "remind"));
        var line = await LineAsync();
        var (instance, entity) = await StartAsync(line.Worker);
        var task = await RequireTaskAsync(line.Lead.Token, instance);
        var definition = (await InstanceAsync(instance)).GetProperty("instance");
        using var listed = await GetAsync("/workflow/definitions?pageSize=200", await AdminAsync());
        var definitionId = Id((await JsonAsync(listed)).GetProperty("items")[0]);
        using var delegated = await DelegateAsync(line.Lead.Token, line.Head.UserId);
        var delegationId = Id(await JsonAsync(delegated));
        var theirAdmin = await GlobexAdminAsync();
        var outsider = await PersonAsync(tenant: Globex);

        var attempts = new (string Name, Func<Task<HttpResponseMessage>> Call)[]
        {
            ("task", () => GetAsync($"/workflow/tasks/{task}", outsider.Token)),
            ("act", () => ActAsync(outsider.Token, task, "approve")),
            ("instance", () => GetAsync($"/workflow/instances/{instance}", theirAdmin)),
            ("withdraw", () => PostAsync($"/workflow/instances/{instance}/withdraw", new { }, outsider.Token)),
            ("reassign", () => PostAsync($"/workflow/tasks/{task}/reassign", new { userId = outsider.UserId, reason = "x" }, theirAdmin)),
            ("assign", () => PostAsync($"/workflow/instances/{instance}/assign", new { userId = outsider.UserId, reason = "x" }, theirAdmin)),
            ("retry", () => PostAsync($"/workflow/instances/{instance}/retry", null, theirAdmin)),
            ("definition", () => GetAsync($"/workflow/definitions/{definitionId}", theirAdmin)),
            ("update", () => PutAsync($"/workflow/definitions/{definitionId}", new { name = "x", stages = new[] { Stage(Manager()) }, rowVersion = 1 }, theirAdmin)),
            ("publish", () => PostAsync($"/workflow/definitions/{definitionId}/publish", new { rowVersion = 1 }, theirAdmin)),
            ("revoke", () => DeleteAsync($"/workflow/delegations/{delegationId}", theirAdmin)),
            ("delivery", () => PostAsync($"/workflow/deliveries/{Guid.NewGuid()}/retry", null, theirAdmin)),
        };
        foreach (var (name, call) in attempts)
        {
            using var response = await call();
            response.StatusCode.Should().Be(HttpStatusCode.NotFound, $"{name} must not reach across tenants");
        }

        foreach (var path in new[] { "/workflow/instances", "/workflow/definitions", "/workflow/deliveries" })
        {
            using var list = await GetAsync(path, theirAdmin);
            (await JsonAsync(list)).GetProperty("total").GetInt64().Should().Be(0, path);
        }

        (await InboxAsync(outsider.Token)).Should().BeEmpty();
        using var delegateAcross = await DelegateAsync(outsider.Token, line.Lead.UserId);
        delegateAcross.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        using var theirDelegations = await GetAsync("/workflow/delegations", theirAdmin);
        (await JsonAsync(theirDelegations)).GetProperty("total").GetInt64().Should().Be(0);
        (await StatusAsync(instance)).Should().Be("pending");
        definition.GetProperty("entityId").GetGuid().Should().Be(entity);
    }

    [Fact]
    public async Task OtherPeoplesTasksAndWorkflows_AreNotFound_InTheSameTenant()
    {
        await PublishAsync(TestWorkflows.Leave, Stage(Manager()));
        var line = await LineAsync();
        var stranger = await PersonAsync();
        var (instance, _) = await StartAsync(line.Worker);
        var task = await RequireTaskAsync(line.Lead.Token, instance);

        using var read = await GetAsync($"/workflow/tasks/{task}", stranger.Token);
        using var workflow = await GetAsync($"/workflow/instances/{instance}", stranger.Token);
        using var subjectReads = await GetAsync($"/workflow/instances/{instance}", line.Worker.Token);
        using var approverReads = await GetAsync($"/workflow/instances/{instance}", line.Lead.Token);

        read.StatusCode.Should().Be(HttpStatusCode.NotFound);
        workflow.StatusCode.Should().Be(HttpStatusCode.NotFound);
        await ExpectAsync(await ActAsync(stranger.Token, task, "approve"), HttpStatusCode.NotFound, "workflow.task.not_found");
        await ExpectAsync(await ActAsync(line.Worker.Token, task, "approve"), HttpStatusCode.NotFound, "workflow.task.not_found");
        subjectReads.StatusCode.Should().Be(HttpStatusCode.OK, "the requester follows their own request");
        approverReads.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AdministrativeAccess_StopsAtTheDataScope()
    {
        await PublishAsync(TestWorkflows.Leave, Stage(Manager()));
        var lead = await PersonAsync();
        var inA = await PersonAsync(lead, unit: Acme.DepartmentAId);
        var inB = await PersonAsync(lead, unit: Acme.DepartmentBId);
        var (a, _) = await StartAsync(inA);
        var (b, _) = await StartAsync(inB);
        var scoped = await UserAsync(
            ScopeType.Department, Acme.DepartmentBId, Permissions.Workflow.InstanceView, Permissions.Workflow.InstanceManage, Permissions.Workflow.DelegationManage);

        using var list = await GetAsync("/workflow/instances?pageSize=200", scoped.Token);
        var seen = (await JsonAsync(list)).GetProperty("items").EnumerateArray().Select(Id).ToList();
        seen.Should().Contain(b).And.NotContain(a);

        using var inside = await GetAsync($"/workflow/instances/{b}", scoped.Token);
        using var outside = await GetAsync($"/workflow/instances/{a}", scoped.Token);
        inside.StatusCode.Should().Be(HttpStatusCode.OK);
        outside.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var task = await RequireTaskAsync(lead.Token, a);
        await ExpectAsync(
            await PostAsync($"/workflow/tasks/{task}/reassign", new { userId = inB.UserId, reason = "x" }, scoped.Token),
            HttpStatusCode.NotFound, "workflow.task.not_found");
        await ExpectAsync(await DelegateAsync(scoped.Token, inB.UserId, delegator: inA.UserId), HttpStatusCode.Forbidden, "workflow.delegation.not_allowed");
        using var allowed = await DelegateAsync(scoped.Token, inA.UserId, delegator: inB.UserId);
        allowed.StatusCode.Should().Be(HttpStatusCode.Created, await allowed.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AdministrativeEndpoints_NeedTheirPermission_AndEveryEndpointNeedsASignedInUser()
    {
        var nobody = await PersonAsync();
        var id = Guid.NewGuid();
        var guarded = new Func<string?, Task<HttpResponseMessage>>[]
        {
            t => GetAsync("/workflow/keys", t),
            t => GetAsync("/workflow/definitions", t),
            t => GetAsync($"/workflow/definitions/{id}", t),
            t => PostAsync("/workflow/definitions", new { }, t),
            t => PutAsync($"/workflow/definitions/{id}", new { }, t),
            t => PostAsync($"/workflow/definitions/{id}/publish", new { }, t),
            t => GetAsync("/workflow/instances", t),
            t => PostAsync($"/workflow/tasks/{id}/reassign", new { }, t),
            t => PostAsync($"/workflow/instances/{id}/assign", new { }, t),
            t => PostAsync($"/workflow/instances/{id}/retry", null, t),
            t => GetAsync("/workflow/deliveries", t),
            t => PostAsync($"/workflow/deliveries/{id}/retry", null, t),
        };
        var ownOnly = new Func<string?, Task<HttpResponseMessage>>[]
        {
            t => GetAsync("/workflow/tasks", t),
            t => GetAsync($"/workflow/tasks/{id}", t),
            t => PostAsync($"/workflow/tasks/{id}/actions", new { action = "approve" }, t),
            t => GetAsync($"/workflow/instances/{id}", t),
            t => PostAsync($"/workflow/instances/{id}/withdraw", new { }, t),
            t => GetAsync("/workflow/delegations", t),
            t => PostAsync("/workflow/delegations", new { }, t),
            t => DeleteAsync($"/workflow/delegations/{id}", t),
        };

        foreach (var call in guarded)
        {
            using var forbidden = await call(nobody.Token);
            forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        foreach (var call in guarded.Concat(ownOnly))
        {
            using var anonymous = await call(null);
            anonymous.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.BadRequest);
        }
    }

    [Theory]
    [InlineData("/workflow/definitions")]
    [InlineData("/workflow/delegations")]
    public async Task ANullBody_Is422(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Api + path)
        {
            Content = new StringContent("null", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", await AdminAsync());

        await ExpectAsync(await Host.Client.SendAsync(request), HttpStatusCode.UnprocessableEntity, "workflow.request.body_required");
    }
}
