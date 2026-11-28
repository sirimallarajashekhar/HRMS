using Hrms.Audit.Persistence;
using Hrms.Contracts;
using Hrms.Http;
using Hrms.Http.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Hrms.Audit.Api;

public static class AuditEndpoints
{
    /// <summary>History is read-only, so no request needs a large body.</summary>
    public const long MaxBodyBytes = 16 * 1024;

    /// <summary>What the API host needs to serve history. The modules register their own <see cref="IAuditRecordAccess"/>.</summary>
    public static IServiceCollection AddAuditHistory(this IServiceCollection services, string? mysqlConnectionString)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddDbContext<AuditDbContext>(options => AuditDbContext.UseAuditMySql(options, mysqlConnectionString));
        services.AddScoped<AuditHistoryService>();
        return services;
    }

    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapGet("/audit/{entityType}/{recordId:guid}", async (
                string entityType, Guid recordId, DateTimeOffset? from, DateTimeOffset? to, int? page, int? pageSize,
                AuditHistoryService history, IAuthorizationService authorization, HttpContext http, CancellationToken ct) =>
            {
                async Task<bool> Holds(string permission) =>
                    (await authorization.AuthorizeAsync(http.User, null, new RequirePermissionAttribute(permission).Policy!)).Succeeded;

                var query = new AuditHistoryQuery(from?.UtcDateTime, to?.UtcDateTime, page, pageSize);
                var result = await history.GetAsync(entityType, recordId, query, Holds, ct);
                http.Response.Headers.CacheControl = "no-store";
                return result.ToHttpResult(view => Results.Ok(view));
            })
            .RequirePermission(Permissions.Audit.LogView)
            .WithMetadata(new RequestSizeLimitAttribute(MaxBodyBytes))
            .WithTags("Audit")
            .WithSummary("The change history of a record and what belongs to it, newest first. Only what the caller may see is returned.");

        return routes;
    }
}
