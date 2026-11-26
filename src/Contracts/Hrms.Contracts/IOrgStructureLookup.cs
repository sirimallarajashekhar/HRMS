namespace Hrms.Contracts;

/// <summary>What a lookup found for an organisation-structure master.</summary>
public enum MasterStatus
{
    /// <summary>No such record for the current tenant (an id from another tenant counts as missing).</summary>
    NotFound = 0,

    /// <summary>The record exists but is inactive: it stays valid where used, and cannot be chosen for new assignments.</summary>
    Inactive = 1,

    Active = 2,
}

public sealed record LegalEntitySummary(Guid Id, string Code, string CountryCode, string CurrencyCode, string CountryPackCode);

/// <summary>
/// The Core module's answers about organisation structure, for modules that reference it (the employee module checks job references and
/// expands department scopes through it). It exists so no module reads another module's tables.
/// </summary>
public interface IOrgStructureLookup
{
    /// <param name="kind">One of <see cref="MasterKinds"/>.</param>
    Task<MasterStatus> GetStatusAsync(string kind, Guid id, CancellationToken cancellationToken);

    Task<LegalEntitySummary?> GetLegalEntityAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The unit and every unit below it on a date (empty when the unit does not exist for the tenant).</summary>
    Task<IReadOnlySet<Guid>> GetOrgUnitSubtreeAsync(Guid orgUnitId, DateOnly asOf, CancellationToken cancellationToken);
}

/// <summary>The tenants a background job must visit (active ones only).</summary>
public interface IActiveTenantSource
{
    Task<IReadOnlyList<Guid>> ListActiveTenantIdsAsync(CancellationToken cancellationToken);
}

/// <summary>Whether an employee record exists for the current tenant; the identity module asks before linking an account to one.</summary>
public interface IEmployeeDirectory
{
    Task<bool> ExistsAsync(Guid employeeId, CancellationToken cancellationToken);
}

/// <summary>
/// Validates a national or statutory id for a country. Country packs register the real rules (src/CountryPacks); until one exists
/// for a country, the default only checks that the value is present and of a sane length.
/// </summary>
public interface IStatutoryIdValidator
{
    /// <returns>Null when the value is acceptable, otherwise a short reason a person can act on.</returns>
    Task<string?> ValidateAsync(string countryPackCode, string idType, string value, CancellationToken cancellationToken);
}
