namespace Hrms.Contracts;

/// <summary>Claim types in the access token: written by the identity server, read by every API.</summary>
public static class HrmsClaims
{
    /// <summary>The user account id.</summary>
    public const string Subject = "sub";

    /// <summary>The tenant the user belongs to. A request is served only when this matches the tenant it resolved to.</summary>
    public const string TenantId = "tenant_id";

    /// <summary>One claim per role code held by the user.</summary>
    public const string Role = "role";
}
