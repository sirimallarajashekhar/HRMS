using Hrms.Contracts;
using Hrms.Http;
using Hrms.Http.Authorization;
using Hrms.SharedKernel;
using Hrms.Workflow.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Hrms.Workflow.Api;

public sealed record WithdrawRequest(string? Comment);

/// <summary>
/// The workflow API under /workflow. There is no endpoint that starts a workflow: only a module's server code can, through
/// IWorkflowService. Endpoints that work on the caller's own approvals need no permission; each checks the signed-in user against
/// the task or request, and answers 404 for what is not theirs.
/// </summary>
public static class WorkflowEndpoints
{
    /// <summary>Largest request body these endpoints accept.</summary>
    public const long MaxBodyBytes = 64 * 1024;

    public static IEndpointRouteBuilder MapWorkflowEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/workflow").WithTags("Workflow").WithMetadata(new RequestSizeLimitAttribute(MaxBodyBytes));
        MapTasks(group);
        MapInstances(group);
        MapDefinitions(group);
        MapDelegations(group);
        MapDeliveries(group);
        return routes;
    }

    private static IResult BodyRequired() => Error.Validation("workflow.request.body_required", "A JSON body is required.").ToProblem();

    // An integration client has a token but is not a person: it has no approvals of its own and no data scope.
    private static IResult UserRequired() => Error.Forbidden("workflow.user_required", "This needs a signed-in user.").ToProblem();

    private static void MapTasks(RouteGroupBuilder group)
    {
        group.MapGet("/tasks", async (ICurrentUser user, IWorkflowInboxService inbox, bool? done, int? page, int? pageSize, CancellationToken ct) =>
                user.UserId is { } me
                    ? Results.Ok(await inbox.ListTasksAsync(me, done ?? false, new PageRequest(page, pageSize), ct))
                    : UserRequired())
            .AllowAnyAuthenticatedUser()
            .WithSummary("The caller's approval inbox: pending tasks, their own and delegated to them. done=true lists their closed tasks.");

        group.MapGet("/tasks/{id:guid}", async (Guid id, ICurrentUser user, IWorkflowInboxService inbox, CancellationToken ct) =>
                user.UserId is { } me ? (await inbox.GetTaskAsync(me, id, ct)).ToHttpResult(view => Results.Ok(view)) : UserRequired())
            .AllowAnyAuthenticatedUser()
            .WithSummary("One of the caller's tasks (own or delegated) with the request's routing fields.");

        group.MapPost("/tasks/{id:guid}/actions", async (Guid id, TaskActionRequest? request, ICurrentUser user, IWorkflowInboxService inbox, CancellationToken ct) =>
            {
                if (user.UserId is not { } me)
                {
                    return UserRequired();
                }

                return request is null ? BodyRequired() : (await inbox.ActAsync(me, id, request, ct)).ToHttpResult(view => Results.Ok(view));
            })
            .AllowAnyAuthenticatedUser()
            .WithSummary("Approves, rejects, sends back or comments on a task. Only the assignee or their delegate, and never on their own request.");

        group.MapPost("/tasks/{id:guid}/reassign", async (Guid id, AssignRequest? request, ICurrentUser user, IWorkflowAdminService admin, CancellationToken ct) =>
            {
                if (user.UserId is not { } me)
                {
                    return UserRequired();
                }

                return request is null ? BodyRequired() : (await admin.ReassignTaskAsync(me, id, request, ct)).ToHttpResult(view => Results.Ok(view));
            })
            .RequirePermission(Permissions.Workflow.InstanceManage)
            .WithSummary("Gives a pending task to another user, with a reason. Never to yourself, the requester or the person the request is about.");
    }

    private static void MapInstances(RouteGroupBuilder group)
    {
        group.MapGet("/instances", async (
                ICurrentUser user, IWorkflowAdminService admin, string? status, string? workflowKey, int? page, int? pageSize, CancellationToken ct) =>
                user.UserId is { } me
                    ? (await admin.ListInstancesAsync(me, status, workflowKey, new PageRequest(page, pageSize), ct)).ToHttpResult(view => Results.Ok(view))
                    : UserRequired())
            .RequirePermission(Permissions.Workflow.InstanceView)
            .WithSummary("Workflows about employees in the caller's data scope, newest first, filtered by status and key.");

        group.MapGet("/instances/{id:guid}", async (Guid id, ICurrentUser user, IWorkflowInboxService inbox, CancellationToken ct) =>
                user.UserId is { } me ? (await inbox.GetInstanceAsync(me, id, ct)).ToHttpResult(view => Results.Ok(view)) : UserRequired())
            .AllowAnyAuthenticatedUser()
            .WithSummary("A workflow with its tasks and history: for the requester, its approvers, and holders of workflow.instance.view within their scope.");

        group.MapPost("/instances/{id:guid}/withdraw", async (Guid id, WithdrawRequest? request, ICurrentUser user, IWorkflowInboxService inbox, CancellationToken ct) =>
                user.UserId is { } me
                    ? (await inbox.WithdrawAsync(me, id, request?.Comment, ct)).ToHttpResult(view => Results.Ok(view))
                    : UserRequired())
            .AllowAnyAuthenticatedUser()
            .WithSummary("Withdraws the caller's own request before its final decision.");

        group.MapPost("/instances/{id:guid}/assign", async (Guid id, AssignRequest? request, ICurrentUser user, IWorkflowAdminService admin, CancellationToken ct) =>
            {
                if (user.UserId is not { } me)
                {
                    return UserRequired();
                }

                return request is null ? BodyRequired() : (await admin.AssignBlockedAsync(me, id, request, ct)).ToHttpResult(view => Results.Ok(view));
            })
            .RequirePermission(Permissions.Workflow.InstanceManage)
            .WithSummary("Names an approver for a blocked workflow's current stage, with a reason.");

        group.MapPost("/instances/{id:guid}/retry", async (Guid id, ICurrentUser user, IWorkflowAdminService admin, CancellationToken ct) =>
                user.UserId is { } me ? (await admin.RetryBlockedAsync(me, id, ct)).ToHttpResult(view => Results.Ok(view)) : UserRequired())
            .RequirePermission(Permissions.Workflow.InstanceManage)
            .WithSummary("Looks for approvers again on a blocked workflow, after the missing data was fixed.");
    }

    private static void MapDefinitions(RouteGroupBuilder group)
    {
        group.MapGet("/keys", (IWorkflowDefinitionService definitions) => Results.Ok(definitions.Keys()))
            .RequirePermission(Permissions.Workflow.DefinitionView)
            .WithSummary("The workflows the system's modules declare, with the fields conditions and approver rules may use.");

        group.MapGet("/definitions", async (IWorkflowDefinitionService definitions, string? workflowKey, int? page, int? pageSize, CancellationToken ct) =>
                Results.Ok(await definitions.ListAsync(workflowKey, new PageRequest(page, pageSize), ct)))
            .RequirePermission(Permissions.Workflow.DefinitionView)
            .WithSummary("Definition versions, paged.");

        group.MapGet("/definitions/{id:guid}", async (Guid id, IWorkflowDefinitionService definitions, CancellationToken ct) =>
                (await definitions.GetAsync(id, ct)).ToHttpResult(view => Results.Ok(view)))
            .RequirePermission(Permissions.Workflow.DefinitionView)
            .WithSummary("One version with its stages.");

        group.MapPost("/definitions", async (CreateDefinitionRequest? request, HttpRequest http, IWorkflowDefinitionService definitions, CancellationToken ct) =>
                request is null
                    ? BodyRequired()
                    : (await definitions.CreateDraftAsync(request, ct)).ToHttpResult(view => Results.Created($"{http.Path.Value!.TrimEnd('/')}/{view.Id}", view)))
            .RequirePermission(Permissions.Workflow.DefinitionManage)
            .WithSummary("Creates the next version of a workflow as a draft. At least one stage must have no condition.");

        group.MapPut("/definitions/{id:guid}", async (Guid id, UpdateDefinitionRequest? request, IWorkflowDefinitionService definitions, CancellationToken ct) =>
                request is null ? BodyRequired() : (await definitions.UpdateDraftAsync(id, request, ct)).ToHttpResult(view => Results.Ok(view)))
            .RequirePermission(Permissions.Workflow.DefinitionManage)
            .WithSummary("Replaces a draft's name and stages; send the rowVersion you read. A published version cannot change.");

        group.MapPost("/definitions/{id:guid}/publish", async (Guid id, PublishDefinitionRequest? request, IWorkflowDefinitionService definitions, CancellationToken ct) =>
                request is null ? BodyRequired() : (await definitions.PublishAsync(id, request, ct)).ToHttpResult(view => Results.Ok(view)))
            .RequirePermission(Permissions.Workflow.DefinitionManage)
            .WithSummary("Publishes a draft from a date (default today). Requests already running keep their version.");
    }

    private static void MapDelegations(RouteGroupBuilder group)
    {
        group.MapGet("/delegations", async (ICurrentUser user, IDelegationService delegations, int? page, int? pageSize, CancellationToken ct) =>
                user.UserId is { } me ? Results.Ok(await delegations.ListAsync(me, new PageRequest(page, pageSize), ct)) : UserRequired())
            .AllowAnyAuthenticatedUser()
            .WithSummary("The caller's delegations, given and received.");

        group.MapPost("/delegations", async (CreateDelegationRequest? request, HttpRequest http, ICurrentUser user, IDelegationService delegations, CancellationToken ct) =>
            {
                if (user.UserId is not { } me)
                {
                    return UserRequired();
                }

                return request is null
                    ? BodyRequired()
                    : (await delegations.CreateAsync(me, request, ct)).ToHttpResult(view => Results.Created($"{http.Path.Value!.TrimEnd('/')}/{view.Id}", view));
            })
            .AllowAnyAuthenticatedUser()
            .WithSummary("Delegates approvals for up to 90 days. For another person it needs workflow.delegation.manage, and the delegate cannot be the caller.");

        group.MapDelete("/delegations/{id:guid}", async (Guid id, ICurrentUser user, IDelegationService delegations, CancellationToken ct) =>
                user.UserId is { } me ? (await delegations.RevokeAsync(me, id, ct)).ToHttpResult(view => Results.Ok(view)) : UserRequired())
            .AllowAnyAuthenticatedUser()
            .WithSummary("Revokes a delegation: either side, or workflow.delegation.manage.");
    }

    private static void MapDeliveries(RouteGroupBuilder group)
    {
        group.MapGet("/deliveries", async (ICurrentUser user, IWorkflowAdminService admin, int? page, int? pageSize, CancellationToken ct) =>
                user.UserId is { } me
                    ? (await admin.ListFailedDeliveriesAsync(me, new PageRequest(page, pageSize), ct)).ToHttpResult(view => Results.Ok(view))
                    : UserRequired())
            .RequirePermission(Permissions.Workflow.InstanceManage)
            .WithSummary("Results the owning module has not accepted after every automatic retry.");

        group.MapPost("/deliveries/{id:guid}/retry", async (Guid id, ICurrentUser user, IWorkflowAdminService admin, CancellationToken ct) =>
                user.UserId is { } me ? (await admin.RetryDeliveryAsync(me, id, ct)).ToHttpResult(() => Results.NoContent()) : UserRequired())
            .RequirePermission(Permissions.Workflow.InstanceManage)
            .WithSummary("Tries a given-up delivery again.");
    }
}
