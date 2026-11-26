using System.Collections.Immutable;
using System.Text.Json;

namespace Hrms.Identity.Application;

/// <summary>
/// An integration (machine) client is bound to exactly one tenant and a fixed set of roles, stored as properties on its OAuth
/// application. A client without a valid tenant binding can never be issued a token, so a mis-registered client fails closed.
/// </summary>
public static class OAuthClientBinding
{
    public const string TenantProperty = "tenant_id";
    public const string RolesProperty = "roles";

    /// <summary>The tenant and roles an integration client is bound to, or null if it has no valid binding.</summary>
    public static (Guid TenantId, IReadOnlyList<string> Roles)? Read(ImmutableDictionary<string, JsonElement> properties)
    {
        ArgumentNullException.ThrowIfNull(properties);

        if (!properties.TryGetValue(TenantProperty, out var tenant)
            || tenant.ValueKind != JsonValueKind.String
            || !tenant.TryGetGuid(out var tenantId)
            || tenantId == Guid.Empty)
        {
            return null;
        }

        var roles = properties.TryGetValue(RolesProperty, out var element) && element.ValueKind == JsonValueKind.Array
            ? element.EnumerateArray().Select(item => item.GetString()).OfType<string>().ToArray()
            : [];
        return (tenantId, roles);
    }
}
