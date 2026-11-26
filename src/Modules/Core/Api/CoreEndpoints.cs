using Hrms.Contracts;
using Hrms.Http.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Hrms.Core.Api;

public static class CoreEndpoints
{
    public static IEndpointRouteBuilder MapCoreEndpoints(this IEndpointRouteBuilder routes)
    {
        // Anonymous on purpose: deny-by-default authorization arrives with P0.4.
        routes.MapGet("/ping", (TimeProvider time) => TypedResults.Ok(new PingResponse("ok", time.GetUtcNow())))
            .WithName("Ping")
            .WithSummary("Liveness probe for the versioned API surface.")
            .AllowAnonymous()
            .AllowWithoutTenant();

        return routes;
    }
}
