namespace Hrms.Contracts;

/// <summary>How far a role reaches for a user: whose records a permission applies to.</summary>
public enum ScopeKind
{
    /// <summary>Only the user own records.</summary>
    Self = 1,

    /// <summary>The people who report directly to the user.</summary>
    DirectReports = 2,

    /// <summary>Everyone below the user in the reporting chain.</summary>
    AllReports = 3,

    /// <summary>One department (the value is the org unit id).</summary>
    Department = 4,

    /// <summary>One location (the value is the location id).</summary>
    Location = 5,

    /// <summary>One legal entity (the value is the legal entity id).</summary>
    LegalEntity = 6,

    /// <summary>Everyone in the tenant.</summary>
    Tenant = 7,
}

/// <summary>One slice of data a user may reach: a kind of scope and, for department, location and legal entity, which one.</summary>
public sealed record DataScopeGrant(ScopeKind Kind, Guid? Value = null);

/// <summary>
/// The data a user may reach for one permission, described rather than listed. A tenant can have 20,000 employees, so handlers apply
/// the scope as a predicate (or ask <see cref="IDataScopeService.GetVisibleEmployeeIdsAsync"/> for a bounded set) instead of
/// materialising everyone. An empty scope means no access: scopes are deny by default.
/// </summary>
public sealed record DataScope(Guid UserId, IReadOnlyList<DataScopeGrant> Grants)
{
    public static DataScope None(Guid userId) => new(userId, []);

    public bool IsEmpty => Grants.Count == 0;

    public bool IsTenantWide => Grants.Any(grant => grant.Kind == ScopeKind.Tenant);
}
