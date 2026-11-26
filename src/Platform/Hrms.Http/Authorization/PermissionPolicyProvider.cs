using System.Collections.Concurrent;
using Hrms.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Hrms.Http.Authorization;

/// <summary>Builds one policy per permission code on demand (and once), so attributes need no up-front policy registration.</summary>
public sealed class PermissionPolicyProvider(IOptions<AuthorizationOptions> options) : DefaultAuthorizationPolicyProvider(options)
{
    public const string AuthenticatedOnlyPolicy = "hrms:authenticated";

    private readonly ConcurrentDictionary<string, AuthorizationPolicy> _policies = new(StringComparer.Ordinal);

    public override Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (policyName == AuthenticatedOnlyPolicy)
        {
            return Task.FromResult<AuthorizationPolicy?>(_policies.GetOrAdd(policyName, _ => Build(permission: null)));
        }

        if (policyName.StartsWith(RequirePermissionAttribute.PolicyPrefix, StringComparison.Ordinal))
        {
            var permission = policyName[RequirePermissionAttribute.PolicyPrefix.Length..];
            if (!Permissions.IsKnown(permission))
            {
                throw new InvalidOperationException(
                    $"'{permission}' is not in the permission catalogue (Hrms.Contracts.Permissions). Add it there or fix the typo.");
            }

            return Task.FromResult<AuthorizationPolicy?>(_policies.GetOrAdd(policyName, _ => Build(permission)));
        }

        return base.GetPolicyAsync(policyName);
    }

    public static AuthorizationPolicy Build(string? permission) => new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .AddRequirements(new PermissionRequirement(permission))
        .Build();
}
