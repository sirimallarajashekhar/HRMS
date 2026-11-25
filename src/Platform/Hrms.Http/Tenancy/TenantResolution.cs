using Hrms.SharedKernel;

namespace Hrms.Http.Tenancy;

public enum TenantResolutionOutcome
{
    /// <summary>The request carries no tenant.</summary>
    NotProvided,

    /// <summary>The request names a tenant but not in a usable form.</summary>
    Malformed,

    Resolved,
}

public sealed record TenantResolution(TenantResolutionOutcome Outcome, TenantId TenantId = default)
{
    public static TenantResolution NotProvided { get; } = new(TenantResolutionOutcome.NotProvided);

    public static TenantResolution Malformed { get; } = new(TenantResolutionOutcome.Malformed);

    public static TenantResolution Resolved(TenantId tenantId) => new(TenantResolutionOutcome.Resolved, tenantId);
}

/// <summary>Works out which tenant an HTTP request is for. P0.4 adds the JWT-claim resolver; until then only the dev header exists.</summary>
public interface ITenantResolver
{
    ValueTask<TenantResolution> ResolveAsync(HttpContext context, CancellationToken cancellationToken);
}
