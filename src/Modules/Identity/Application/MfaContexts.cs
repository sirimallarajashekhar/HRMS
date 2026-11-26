namespace Hrms.Identity.Application;

/// <summary>The encryption contexts for stored MFA values. Binding the tenant and user into the context means a secret copied to
/// another account or tenant does not decrypt.</summary>
public static class MfaContexts
{
    public static string Secret(Guid tenantId, Guid userId) => $"user_account.mfa_secret:{tenantId:N}:{userId:N}";
}
