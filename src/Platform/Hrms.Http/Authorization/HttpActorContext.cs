using Hrms.Contracts;
using Hrms.SharedKernel;
using Microsoft.AspNetCore.Http;

namespace Hrms.Http.Authorization;

/// <summary>
/// The acting user of the current request, read from the token subject, so created_by and updated_by name a person instead of "the
/// system". An anonymous request or a non-user token (an integration client) yields null, which the audit columns treat as the system.
/// </summary>
public sealed class HttpActorContext(IHttpContextAccessor accessor) : IActorContext, ICurrentUser
{
    public Guid? ActorId => Guid.TryParse(accessor.HttpContext?.User.FindFirst(HrmsClaims.Subject)?.Value, out var id) ? id : null;

    public Guid? UserId => ActorId;
}
