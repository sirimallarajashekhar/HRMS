using System.Reflection;

namespace Hrms.Contracts;

/// <summary>
/// The permission catalogue. A permission code is <c>module.entity.action</c> in lower snake case. Each module adds a nested
/// class with its codes as constants when it ships an endpoint; nothing else may invent a code, because
/// <c>[RequirePermission]</c> rejects codes that are not listed here (a typo must not silently deny or, worse, allow).
/// </summary>
public static class Permissions
{
    public static class Identity
    {
        public const string UserView = "identity.user.view";
        public const string UserManage = "identity.user.manage";
        public const string RoleView = "identity.role.view";
        public const string RoleManage = "identity.role.manage";
        public const string SecurityPolicyManage = "identity.security_policy.manage";

        /// <summary>See sensitive fields (bank account, national id, salary) unmasked.</summary>
        public const string SensitiveView = "identity.sensitive.view";
    }

    public static class Core
    {
        public const string LegalEntityView = "core.legal_entity.view";
        public const string LegalEntityManage = "core.legal_entity.manage";

        public const string OrgUnitTypeView = "core.org_unit_type.view";
        public const string OrgUnitTypeManage = "core.org_unit_type.manage";
        public const string OrgUnitView = "core.org_unit.view";
        public const string OrgUnitManage = "core.org_unit.manage";
        public const string LocationView = "core.location.view";
        public const string LocationManage = "core.location.manage";
        public const string GeofenceView = "core.geofence.view";
        public const string GeofenceManage = "core.geofence.manage";
        public const string GradeView = "core.grade.view";
        public const string GradeManage = "core.grade.manage";
        public const string DesignationView = "core.designation.view";
        public const string DesignationManage = "core.designation.manage";
        public const string CostCentreView = "core.cost_centre.view";
        public const string CostCentreManage = "core.cost_centre.manage";
        public const string EmployeeTypeView = "core.employee_type.view";
        public const string EmployeeTypeManage = "core.employee_type.manage";

        /// <summary>Every view permission of the organisation-structure masters (not constants, so the catalogue scan skips them).</summary>
        public static readonly IReadOnlyList<string> OrgStructureView =
        [
            OrgUnitTypeView, OrgUnitView, LocationView, GeofenceView, GradeView, DesignationView, CostCentreView, EmployeeTypeView,
        ];

        /// <summary>Every manage permission of the organisation-structure masters.</summary>
        public static readonly IReadOnlyList<string> OrgStructureManage =
        [
            OrgUnitTypeManage, OrgUnitManage, LocationManage, GeofenceManage, GradeManage, DesignationManage, CostCentreManage, EmployeeTypeManage,
        ];
    }

    public static class Employee
    {
        public const string EmployeeView = "employee.employee.view";
        public const string EmployeeManage = "employee.employee.manage";
        public const string JobManage = "employee.job.manage";
        public const string PersonalView = "employee.personal.view";
        public const string PersonalManage = "employee.personal.manage";
        public const string SeparationManage = "employee.separation.manage";
        public const string StatutoryIdView = "employee.statutory_id.view";
        public const string StatutoryIdManage = "employee.statutory_id.manage";
        public const string DocumentView = "employee.document.view";
        public const string DocumentManage = "employee.document.manage";
        public const string DeviceView = "employee.device.view";
        public const string DeviceManage = "employee.device.manage";
        public const string ConsentView = "employee.consent.view";
        public const string ConsentManage = "employee.consent.manage";
        public const string CodeRuleManage = "employee.code_rule.manage";

        /// <summary>Read access to the people data an auditor or payroll administrator may see (sensitive values stay masked without identity.sensitive.view).</summary>
        public static readonly IReadOnlyList<string> ReadAll =
        [
            EmployeeView, PersonalView, StatutoryIdView, DocumentView, DeviceView, ConsentView,
        ];

        /// <summary>Everything HR administration may do with employees.</summary>
        public static readonly IReadOnlyList<string> ManageAll =
        [
            EmployeeManage, JobManage, PersonalManage, SeparationManage, StatutoryIdManage, DocumentManage, DeviceManage, ConsentManage, CodeRuleManage,
        ];
    }

    /// <summary>Every code in the catalogue, discovered from the nested classes' constants.</summary>
    public static IReadOnlySet<string> All { get; } = typeof(Permissions)
        .GetNestedTypes(BindingFlags.Public | BindingFlags.Static)
        .SelectMany(module => module.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy))
        .Where(field => field is { IsLiteral: true, IsInitOnly: false } && field.FieldType == typeof(string))
        .Select(field => (string)field.GetRawConstantValue()!)
        .ToHashSet(StringComparer.Ordinal);

    public static bool IsKnown(string? code) => code is not null && All.Contains(code);
}
