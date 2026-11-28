namespace Hrms.Http.Authorization;

/// <summary>What the audit log takes from a request besides the caller's identity.</summary>
public static class AuditRequest
{
    /// <summary>Optional free text saying why a change is made. It is stored with every record the request changes.</summary>
    public const string ReasonHeader = "X-Change-Reason";

    /// <summary>The key under which the host's correlation middleware leaves the request's correlation id.</summary>
    public const string CorrelationIdItem = "hrms.correlation_id";
}
