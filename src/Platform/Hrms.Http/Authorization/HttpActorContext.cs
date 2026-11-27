using Hrms.Contracts;
using Hrms.SharedKernel;
using Microsoft.AspNetCore.Http;

namespace Hrms.Http.Authorization;

/// <summary>
/// The acting user of the current request, read from the token subject, so created_by and updated_by name a person instead of "the
/// system". An anonymous request or a non-user token (an integration client) yields null, which the audit columns treat as the system;
/// <see cref="Origin"/> tells the audit log which of those it was.
/// </summary>
public sealed class HttpActorContext(IHttpContextAccessor accessor) : IActorContext, ICurrentUser
{
    public Guid? ActorId => Guid.TryParse(accessor.HttpContext?.User.FindFirst(HrmsClaims.Subject)?.Value, out var id) ? id : null;

    public Guid? UserId => ActorId;

    public AuditOrigin Origin
    {
        get
        {
            // No request at all is a command-line task such as bootstrap-tenant.
            if (accessor.HttpContext is not { } http)
            {
                return AuditOrigin.System;
            }

            var subject = http.User.Identity?.IsAuthenticated == true ? http.User.FindFirst(HrmsClaims.Subject)?.Value : null;
            var (type, clientId) = subject switch
            {
                null => (AuditActorType.Anonymous, null),
                _ when ActorId is not null => (AuditActorType.User, null),
                _ => (AuditActorType.Client, subject),
            };

            // Behind a proxy this is the proxy's address until forwarded headers are configured (see docs/PROGRESS.md).
            return new AuditOrigin(
                type,
                clientId,
                http.Connection.RemoteIpAddress?.ToString(),
                CleanReason(http.Request.Headers[AuditRequest.ReasonHeader].FirstOrDefault()),
                http.Items[AuditRequest.CorrelationIdItem] as string);
        }
    }

    /// <summary>Control characters are dropped so the text cannot forge lines wherever history is exported or displayed.</summary>
    private static string? CleanReason(string? reason)
    {
        var cleaned = new string((reason ?? string.Empty).Where(c => !char.IsControl(c)).ToArray()).Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }
}
