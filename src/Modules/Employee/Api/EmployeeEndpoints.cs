using Hrms.Contracts;
using Hrms.Employee.Application;
using Hrms.Http;
using Hrms.Http.Authorization;
using Hrms.SharedKernel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Hrms.Employee.Api;

/// <summary>
/// The employee endpoints under /api/v1. Each states its permission; on top of that the data scope of the caller's roles decides which
/// employees are reachable, and an employee outside it is simply not found (404). Request bodies are capped, lists are paged (at most 200),
/// changes carry the rowVersion they were based on, and sensitive values (statutory ids, personal email and phone) are masked unless the
/// caller holds the sensitive-view permission.
/// </summary>
public static class EmployeeEndpoints
{
    /// <summary>Largest request body these endpoints accept.</summary>
    public const long MaxBodyBytes = 64 * 1024;

    public static IEndpointRouteBuilder MapEmployeeEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        MapEmployees(routes);
        MapStatutoryIds(routes);
        MapDocuments(routes);
        MapDevices(routes);
        MapConsents(routes);
        MapCodeRules(routes);
        return routes;
    }

    private static IResult BodyRequired() => Error.Validation("employee.request.body_required", "A JSON body is required.").ToProblem();

    private static string Location(HttpRequest http, object id) => $"{http.Path.Value!.TrimEnd('/')}/{id}";

    private static void MapEmployees(IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/employees").WithTags("Employees").WithMetadata(new RequestSizeLimitAttribute(MaxBodyBytes));

        group.MapGet(string.Empty, async (
                IEmployeeService service, int? page, int? pageSize, string? search, string? status, Guid? legalEntityId, Guid? locationId, Guid? orgUnitId,
                bool? includeSubUnits, Guid? designationId, Guid? gradeId, Guid? employeeTypeId, Guid? managerId, DateOnly? asOf, CancellationToken ct) =>
                (await service.ListAsync(
                    new EmployeeQuery(page, pageSize, search, status, legalEntityId, locationId, orgUnitId, includeSubUnits ?? true, designationId, gradeId, employeeTypeId, managerId, asOf), ct))
                .ToHttpResult(result => Results.Ok(result)))
            .RequirePermission(Permissions.Employee.EmployeeView)
            .WithSummary("Lists the employees the caller's data scope reaches, paged (at most 200 per page). Search matches the start of a code or name; status is active (default), separated, notJoined or all; orgUnitId includes sub-units unless includeSubUnits=false.");

        group.MapPost(string.Empty, async (CreateEmployeeRequest? request, HttpRequest http, IEmployeeService service, CancellationToken ct) =>
                request is null ? BodyRequired() : (await service.CreateAsync(request, ct)).ToHttpResult(view => Results.Created(Location(http, view.Id), view)))
            .RequirePermission(Permissions.Employee.EmployeeManage)
            .WithSummary("Creates an employee with personal details and the first job. The code comes from the legal entity's code rule; the first job starts on the joining date.");

        group.MapGet("/{id:guid}/profile", async (
                Guid id, DateOnly? asOf, IEmployeeService service, IAuthorizationService authorization, ISensitiveDataAccess sensitive, HttpContext http, CancellationToken ct) =>
            {
                async Task<bool> Has(string permission) =>
                    (await authorization.AuthorizeAsync(http.User, null, new RequirePermissionAttribute(permission).Policy!)).Succeeded;

                var profileAccess = new ProfileAccess(
                    await Has(Permissions.Employee.PersonalView),
                    await Has(Permissions.Employee.StatutoryIdView),
                    await Has(Permissions.Employee.DocumentView),
                    await Has(Permissions.Employee.DeviceView),
                    await Has(Permissions.Employee.ConsentView),
                    await sensitive.CanViewSensitiveAsync(ct));

                return (await service.GetProfileAsync(id, asOf, profileAccess, ct)).ToHttpResult(view => Results.Ok(view));
            })
            .RequirePermission(Permissions.Employee.EmployeeView)
            .WithSummary("The employee with the tabs the caller may see (a tab without permission is null). asOf picks the job as of a date.");

        group.MapPut("/{id:guid}/personal", async (Guid id, UpdatePersonalRequest? request, IEmployeeService service, ISensitiveDataAccess sensitive, CancellationToken ct) =>
                request is null ? BodyRequired() : (await service.UpdatePersonalAsync(id, request, await sensitive.CanViewSensitiveAsync(ct), ct)).ToHttpResult(view => Results.Ok(view)))
            .RequirePermission(Permissions.Employee.PersonalManage)
            .WithSummary("Replaces the personal details; send the personal rowVersion from the profile.");

        group.MapGet("/{id:guid}/jobs", async (Guid id, IEmployeeService service, CancellationToken ct) =>
                (await service.JobHistoryAsync(id, ct)).ToHttpResult(history => Results.Ok(history)))
            .RequirePermission(Permissions.Employee.EmployeeView)
            .WithSummary("The job history, oldest first.");

        group.MapPost("/{id:guid}/jobs", async (Guid id, ChangeJobRequest? request, IEmployeeService service, CancellationToken ct) =>
                request is null ? BodyRequired() : (await service.ChangeJobAsync(id, request, ct)).ToHttpResult(view => Results.Ok(view)))
            .RequirePermission(Permissions.Employee.JobManage)
            .WithSummary("Transfers or promotes the employee from a date after their latest job change (transfer, promotion or change). History is kept; a future date takes effect then.");

        group.MapPost("/{id:guid}/confirm", async (Guid id, ConfirmEmployeeRequest? request, IEmployeeService service, CancellationToken ct) =>
                request is null ? BodyRequired() : (await service.ConfirmAsync(id, request, ct)).ToHttpResult(view => Results.Ok(view)))
            .RequirePermission(Permissions.Employee.EmployeeManage)
            .WithSummary("Confirms the employee after probation (the date defaults to today).");

        group.MapPut("/{id:guid}/separation", async (Guid id, SeparationRequest? request, ISeparationService service, CancellationToken ct) =>
                request is null ? BodyRequired() : (await service.SetAsync(id, request, ct)).ToHttpResult(view => Results.Ok(view)))
            .RequirePermission(Permissions.Employee.SeparationManage)
            .WithSummary("Records or changes the separation. Refused while people still report to the employee on the last working day.");

        group.MapDelete("/{id:guid}/separation", async (Guid id, ISeparationService service, CancellationToken ct) =>
                (await service.WithdrawAsync(id, ct)).ToHttpResult(() => Results.NoContent()))
            .RequirePermission(Permissions.Employee.SeparationManage)
            .WithSummary("Withdraws a separation whose last working day has not passed.");
    }

    private static void MapStatutoryIds(IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/employees/{employeeId:guid}/statutory-ids").WithTags("Statutory ids").WithMetadata(new RequestSizeLimitAttribute(MaxBodyBytes));

        group.MapGet(string.Empty, async (Guid employeeId, IStatutoryIdService service, ISensitiveDataAccess sensitive, CancellationToken ct) =>
                (await service.ListAsync(employeeId, await sensitive.CanViewSensitiveAsync(ct), ct)).ToHttpResult(list => Results.Ok(list)))
            .RequirePermission(Permissions.Employee.StatutoryIdView)
            .WithSummary("The employee's statutory ids; values are masked unless the caller may see sensitive values.");

        group.MapPut("/{idType}", async (Guid employeeId, string idType, SetStatutoryIdRequest? request, IStatutoryIdService service, ISensitiveDataAccess sensitive, CancellationToken ct) =>
                request is null ? BodyRequired() : (await service.SetAsync(employeeId, idType, request, await sensitive.CanViewSensitiveAsync(ct), ct)).ToHttpResult(view => Results.Ok(view)))
            .RequirePermission(Permissions.Employee.StatutoryIdManage)
            .WithSummary("Sets (creates or replaces) the id of that type; the value is validated for the country and stored encrypted.");

        group.MapDelete("/{idType}", async (Guid employeeId, string idType, IStatutoryIdService service, CancellationToken ct) =>
                (await service.DeleteAsync(employeeId, idType, ct)).ToHttpResult(() => Results.NoContent()))
            .RequirePermission(Permissions.Employee.StatutoryIdManage)
            .WithSummary("Removes the id of that type.");
    }

    private static void MapDocuments(IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/employees/{employeeId:guid}/documents").WithTags("Documents").WithMetadata(new RequestSizeLimitAttribute(MaxBodyBytes));

        group.MapGet(string.Empty, async (Guid employeeId, IDocumentService service, CancellationToken ct) =>
                (await service.ListAsync(employeeId, ct)).ToHttpResult(list => Results.Ok(list)))
            .RequirePermission(Permissions.Employee.DocumentView)
            .WithSummary("The documents held for the employee, with whether each has expired.");

        group.MapPost(string.Empty, async (Guid employeeId, CreateDocumentRequest? request, HttpRequest http, IDocumentService service, CancellationToken ct) =>
                request is null ? BodyRequired() : (await service.CreateAsync(employeeId, request, ct)).ToHttpResult(view => Results.Created(Location(http, view.Id), view)))
            .RequirePermission(Permissions.Employee.DocumentManage)
            .WithSummary("Records a document (metadata and an opaque storage key).");

        group.MapPut("/{id:guid}", async (Guid employeeId, Guid id, UpdateDocumentRequest? request, IDocumentService service, CancellationToken ct) =>
                request is null ? BodyRequired() : (await service.UpdateAsync(employeeId, id, request, ct)).ToHttpResult(view => Results.Ok(view)))
            .RequirePermission(Permissions.Employee.DocumentManage)
            .WithSummary("Changes a document; send the rowVersion you read.");

        group.MapDelete("/{id:guid}", async (Guid employeeId, Guid id, IDocumentService service, CancellationToken ct) =>
                (await service.DeleteAsync(employeeId, id, ct)).ToHttpResult(() => Results.NoContent()))
            .RequirePermission(Permissions.Employee.DocumentManage)
            .WithSummary("Removes a document record.");
    }

    private static void MapDevices(IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/employees/{employeeId:guid}/devices").WithTags("Devices").WithMetadata(new RequestSizeLimitAttribute(MaxBodyBytes));

        group.MapGet(string.Empty, async (Guid employeeId, IDeviceService service, CancellationToken ct) =>
                (await service.ListAsync(employeeId, ct)).ToHttpResult(list => Results.Ok(list)))
            .RequirePermission(Permissions.Employee.DeviceView)
            .WithSummary("The devices registered for the employee.");

        group.MapPost(string.Empty, async (Guid employeeId, RegisterDeviceRequest? request, HttpRequest http, IDeviceService service, CancellationToken ct) =>
                request is null ? BodyRequired() : (await service.RegisterAsync(employeeId, request, ct)).ToHttpResult(view => Results.Created(Location(http, view.Id), view)))
            .RequirePermission(Permissions.Employee.DeviceManage)
            .WithSummary("Registers a device (android, ios or web). A device belongs to one employee at a time.");

        group.MapPost("/{id:guid}/revoke", async (Guid employeeId, Guid id, IDeviceService service, CancellationToken ct) =>
                (await service.RevokeAsync(employeeId, id, ct)).ToHttpResult(view => Results.Ok(view)))
            .RequirePermission(Permissions.Employee.DeviceManage)
            .WithSummary("Revokes a device; the record stays.");

        group.MapDelete("/{id:guid}", async (Guid employeeId, Guid id, IDeviceService service, CancellationToken ct) =>
                (await service.DeleteAsync(employeeId, id, ct)).ToHttpResult(() => Results.NoContent()))
            .RequirePermission(Permissions.Employee.DeviceManage)
            .WithSummary("Removes a device record, freeing its id for another employee.");
    }

    private static void MapConsents(IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/employees/{employeeId:guid}/consents").WithTags("Consents").WithMetadata(new RequestSizeLimitAttribute(MaxBodyBytes));

        group.MapGet(string.Empty, async (Guid employeeId, IConsentService service, CancellationToken ct) =>
                (await service.ListAsync(employeeId, ct)).ToHttpResult(list => Results.Ok(list)))
            .RequirePermission(Permissions.Employee.ConsentView)
            .WithSummary("The consents the employee gave or withdrew, oldest first.");

        group.MapPost(string.Empty, async (Guid employeeId, GrantConsentRequest? request, HttpRequest http, IConsentService service, CancellationToken ct) =>
                request is null ? BodyRequired() : (await service.GrantAsync(employeeId, request, ct)).ToHttpResult(view => Results.Created(Location(http, view.Id), view)))
            .RequirePermission(Permissions.Employee.ConsentManage)
            .WithSummary("Records a consent with the version of the text agreed to.");

        group.MapPost("/{id:guid}/withdraw", async (Guid employeeId, Guid id, IConsentService service, CancellationToken ct) =>
                (await service.WithdrawAsync(employeeId, id, ct)).ToHttpResult(view => Results.Ok(view)))
            .RequirePermission(Permissions.Employee.ConsentManage)
            .WithSummary("Withdraws a consent; the record stays.");
    }

    private static void MapCodeRules(IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/employee-code-rules").WithTags("Employee code rules").WithMetadata(new RequestSizeLimitAttribute(MaxBodyBytes));

        group.MapGet(string.Empty, async (ICodeRuleService service, CancellationToken ct) => Results.Ok(await service.ListAsync(ct)))
            .RequirePermission(Permissions.Employee.CodeRuleManage)
            .WithSummary("The employee code rules, one per legal entity.");

        group.MapGet("/{legalEntityId:guid}", async (Guid legalEntityId, ICodeRuleService service, CancellationToken ct) =>
                (await service.GetAsync(legalEntityId, ct)).ToHttpResult(view => Results.Ok(view)))
            .RequirePermission(Permissions.Employee.CodeRuleManage)
            .WithSummary("The code rule of a legal entity, with an example of the next code.");

        group.MapPut("/{legalEntityId:guid}", async (Guid legalEntityId, CodeRuleRequest? request, ICodeRuleService service, CancellationToken ct) =>
                request is null ? BodyRequired() : (await service.SetAsync(legalEntityId, request, ct)).ToHttpResult(view => Results.Ok(view)))
            .RequirePermission(Permissions.Employee.CodeRuleManage)
            .WithSummary("Creates or changes the code rule of a legal entity (prefix, width, next number); the number never goes back.");
    }
}
