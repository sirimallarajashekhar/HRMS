using Hrms.Contracts;
using Hrms.SharedKernel;
using Microsoft.AspNetCore.Mvc;
using IDiagnosticContext = Serilog.IDiagnosticContext;
using Serilog.Context;

namespace Hrms.Http.Tenancy;

/// <summary>
/// Resolves the tenant for every routed request that has not opted out with <see cref="AllowWithoutTenantMetadata"/>,
/// rejects requests without a usable, active tenant, and puts the tenant id in the log scope.
/// It must run after routing so it can see the endpoint's metadata. Unmatched routes pass through to the usual 404.
/// </summary>
public sealed partial class TenantMiddleware(RequestDelegate next, ILogger<TenantMiddleware> logger)
{
    public const string MissingCode = "tenant.missing";
    public const string InvalidCode = "tenant.invalid";
    public const string ForbiddenCode = "tenant.forbidden";

    public async Task InvokeAsync(
        HttpContext context,
        ITenantResolver resolver,
        TenantContext tenantContext,
        IDiagnosticContext diagnostics,
        IProblemDetailsService problems)
    {
        var endpoint = context.GetEndpoint();
        if (endpoint is null || endpoint.Metadata.GetMetadata<AllowWithoutTenantMetadata>() is not null)
        {
            await next(context);
            return;
        }

        var resolution = await resolver.ResolveAsync(context, context.RequestAborted);
        if (resolution.Outcome != TenantResolutionOutcome.Resolved)
        {
            var (code, message) = resolution.Outcome == TenantResolutionOutcome.Malformed
                ? (InvalidCode, "The tenant identifier is not valid.")
                : (MissingCode, "A tenant is required for this request.");
            await RejectAsync(context, problems, StatusCodes.Status400BadRequest, code, message);
            return;
        }

        // Unknown and suspended tenants get the same answer, so callers cannot probe which tenants exist.
        // Resolved here, not injected into InvokeAsync, so endpoints that opt out never touch the database.
        var directory = context.RequestServices.GetRequiredService<ITenantDirectory>();
        if (!await directory.IsActiveAsync(resolution.TenantId.Value, context.RequestAborted))
        {
            await RejectAsync(context, problems, StatusCodes.Status403Forbidden, ForbiddenCode, "This tenant cannot be used.");
            return;
        }

        tenantContext.Set(resolution.TenantId);
        var tenant = resolution.TenantId.ToString();
        diagnostics.Set("TenantId", tenant);

        using (LogContext.PushProperty("TenantId", tenant))
        {
            await next(context);
        }
    }

    private async Task RejectAsync(HttpContext context, IProblemDetailsService problems, int status, string code, string message)
    {
        LogRejected(logger, code);
        context.Response.StatusCode = status;
        await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = message,
                Extensions = { ["code"] = code },
            },
        });
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Request rejected by tenant resolution: {Code}")]
    private static partial void LogRejected(ILogger logger, string code);
}
