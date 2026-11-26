using Hrms.Contracts;
using Hrms.Http;
using Hrms.Http.Authorization;
using Hrms.Identity.Application;
using Hrms.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Hrms.Identity.Api;

public sealed record LinkEmployeeRequest(Guid? EmployeeId);

/// <summary>Administration of user accounts. Only the employee link exists so far; the user and role screens arrive with P0.12.</summary>
public static class UserEndpoints
{
    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapGroup("/users").WithTags("Users").WithMetadata(new RequestSizeLimitAttribute(16 * 1024))
            .MapPut("/{userId:guid}/employee", async (Guid userId, LinkEmployeeRequest? request, IUserEmployeeLinkService links, CancellationToken ct) =>
                request is null
                    ? Error.Validation("identity.request.body_required", "A JSON body is required.").ToProblem()
                    : (await links.SetAsync(userId, request.EmployeeId, ct)).ToHttpResult(link => Results.Ok(link)))
            .RequirePermission(Permissions.Identity.UserManage)
            .WithSummary("Links the account to an employee (or removes the link with a null employeeId). One employee belongs to at most one account.");
        return routes;
    }
}
