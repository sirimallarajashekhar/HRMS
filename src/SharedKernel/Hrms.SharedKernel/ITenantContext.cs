namespace Hrms.SharedKernel;

/// <summary>The tenant the current operation runs for. Resolved once per request (or job) and never changed afterwards.</summary>
public interface ITenantContext
{
    /// <summary>The resolved tenant, or null when none has been resolved (queries then match nothing).</summary>
    TenantId? TenantId { get; }
}

/// <summary>Raised when code that needs a tenant runs without one: a programming or pipeline error, not a business failure.</summary>
public sealed class TenantNotResolvedException : InvalidOperationException
{
    public TenantNotResolvedException()
        : base("No tenant has been resolved for this operation.")
    {
    }
}

public static class TenantContextExtensions
{
    public static TenantId Require(this ITenantContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.TenantId ?? throw new TenantNotResolvedException();
    }
}
