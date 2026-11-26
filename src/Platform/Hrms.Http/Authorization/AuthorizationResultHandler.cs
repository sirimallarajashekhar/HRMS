using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Mvc;

namespace Hrms.Http.Authorization;

/// <summary>Turns authorization failures into problem details with stable codes instead of an empty 401 or 403.</summary>
public sealed class ProblemAuthorizationResultHandler(IProblemDetailsService problems) : IAuthorizationMiddlewareResultHandler
{
    public const string UnauthenticatedCode = "auth.unauthenticated";
    public const string ForbiddenCode = "auth.forbidden";

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorizeResult);

        // A request that matched no endpoint has nothing to protect: let it reach the normal 404 instead of answering 401.
        if (authorizeResult.Succeeded || context.GetEndpoint() is null)
        {
            await next(context);
            return;
        }

        var unauthenticated = context.User.Identity?.IsAuthenticated != true;
        var status = unauthenticated ? StatusCodes.Status401Unauthorized : StatusCodes.Status403Forbidden;
        context.Response.StatusCode = status;
        if (unauthenticated)
        {
            context.Response.Headers.WWWAuthenticate = "Bearer";
        }

        await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = unauthenticated ? "Authentication is required." : "You do not have permission to do this.",
                Extensions = { ["code"] = unauthenticated ? UnauthenticatedCode : ForbiddenCode },
            },
        });
    }
}
