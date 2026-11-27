using Hrms.Contracts;

namespace Hrms.Core.Infrastructure;

/// <summary>The organisation masters are tenant-wide configuration, as on their own endpoints: the permission is the whole check.</summary>
internal sealed class CoreAuditAccess : IAuditRecordAccess
{
    public IReadOnlyList<AuditRecordType> RecordTypes { get; } =
    [
        Single("legal_entity", Permissions.Core.LegalEntityView),
        Single("org_unit_type", Permissions.Core.OrgUnitTypeView),
        new("org_unit",
        [
            new("org_unit", Permissions.Core.OrgUnitView),
            new("org_unit_parent", Permissions.Core.OrgUnitView),
            new("org_unit_head", Permissions.Core.OrgUnitView),
        ]),
        new("location", [new("location", Permissions.Core.LocationView), new("geofence", Permissions.Core.GeofenceView)]),
        Single("grade", Permissions.Core.GradeView),
        Single("designation", Permissions.Core.DesignationView),
        Single("cost_centre", Permissions.Core.CostCentreView),
        Single("employee_type", Permissions.Core.EmployeeTypeView),
    ];

    public Task<bool> IsInScopeAsync(string rootType, Guid recordId, string permission, CancellationToken cancellationToken) =>
        Task.FromResult(true);

    private static AuditRecordType Single(string type, string permission) => new(type, [new(type, permission)]);
}
