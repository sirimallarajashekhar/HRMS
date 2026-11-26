using Hrms.Identity.Application;
using System.Text.Json;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Hrms.Identity.Infrastructure.Security;

/// <summary>
/// Registers the OAuth clients. The two first-party apps (web and mobile) are public clients allowed the password and refresh
/// grants only. An integration is a confidential client bound to exactly one tenant and a fixed set of roles; it gets the
/// client-credentials grant only. Re-running changes nothing.
/// </summary>
public sealed class OAuthClientSeeder(IOpenIddictApplicationManager applications)
{
    public const string WebClientId = "hrms-web";
    public const string MobileClientId = "hrms-mobile";
    public const string TenantProperty = OAuthClientBinding.TenantProperty;
    public const string RolesProperty = OAuthClientBinding.RolesProperty;

    public async Task EnsureFirstPartyClientsAsync(CancellationToken cancellationToken)
    {
        foreach (var (clientId, name) in new[] { (WebClientId, "HRMS web app"), (MobileClientId, "HRMS mobile app") })
        {
            if (await applications.FindByClientIdAsync(clientId, cancellationToken) is not null)
            {
                continue;
            }

            await applications.CreateAsync(new OpenIddictApplicationDescriptor
            {
                ClientId = clientId,
                DisplayName = name,
                ClientType = ClientTypes.Public,
                Permissions =
                {
                    Permissions.Endpoints.Token,
                    Permissions.GrantTypes.Password,
                    Permissions.GrantTypes.RefreshToken,
                    Permissions.Prefixes.Scope + Scopes.OfflineAccess,
                },
            }, cancellationToken);
        }
    }

    /// <summary>Registers a confidential integration client for one tenant. Returns false if the client id already exists.</summary>
    public async Task<bool> RegisterIntegrationAsync(
        string clientId, string clientSecret, Guid tenantId, IReadOnlyCollection<string> roleCodes, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientSecret);
        ArgumentOutOfRangeException.ThrowIfEqual(tenantId, Guid.Empty);
        ArgumentNullException.ThrowIfNull(roleCodes);

        if (await applications.FindByClientIdAsync(clientId, cancellationToken) is not null)
        {
            return false;
        }

        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            ClientSecret = clientSecret,
            DisplayName = clientId,
            ClientType = ClientTypes.Confidential,
            Permissions = { Permissions.Endpoints.Token, Permissions.GrantTypes.ClientCredentials },
        };
        descriptor.Properties[TenantProperty] = JsonSerializer.SerializeToElement(tenantId);
        descriptor.Properties[RolesProperty] = JsonSerializer.SerializeToElement(roleCodes.Distinct(StringComparer.Ordinal).ToArray());

        await applications.CreateAsync(descriptor, cancellationToken);
        return true;
    }
}
