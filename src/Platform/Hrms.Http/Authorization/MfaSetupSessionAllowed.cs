namespace Hrms.Http.Authorization;

/// <summary>Endpoint metadata: the MFA set-up session may call this endpoint.</summary>
public sealed class MfaSetupSessionAllowed
{
    public static readonly MfaSetupSessionAllowed Instance = new();

    private MfaSetupSessionAllowed()
    {
    }
}
