namespace Hrms.SharedKernel;

/// <summary>Category of an expected business failure; drives the HTTP status it maps to.</summary>
public enum ErrorType
{
    /// <summary>Unclassified failure; maps to 500. The default, so a forgotten classification fails safe.</summary>
    Unexpected = 0,

    /// <summary>The request was understood but its content is invalid; maps to 422.</summary>
    Validation,

    /// <summary>The target does not exist for the caller (also used when it exists in another tenant); maps to 404.</summary>
    NotFound,

    /// <summary>The request conflicts with current state (duplicate code, stale row version); maps to 409.</summary>
    Conflict,

    /// <summary>The caller is not allowed to do this; maps to 403.</summary>
    Forbidden,
}
