using Hrms.Contracts;

namespace Hrms.Identity.Domain.Roles;

/// <summary>A role shipped with every tenant and the permissions it starts with.</summary>
public sealed record DefaultRole(string Code, string Name, string Description, bool RequiresMfa, IReadOnlyList<string> PermissionCodes);

/// <summary>
/// The roles every new tenant starts with. This is a draft the product owner approved in place of the missing FSD role table:
/// later modules add their permissions to these roles as they ship. The three administrator roles require MFA.
/// </summary>
public static class DefaultRoles
{
    public const string TenantAdmin = "tenant_admin";
    public const string HrAdmin = "hr_admin";
    public const string PayrollAdmin = "payroll_admin";
    public const string Manager = "manager";
    public const string Employee = "employee";
    public const string Auditor = "auditor";

    public static IReadOnlyList<DefaultRole> All { get; } =
    [
        new(TenantAdmin, "Tenant administrator", "Full control of the tenant, including users, roles and security policy.", true,
            [.. Permissions.All.Order(StringComparer.Ordinal)]),
        new(HrAdmin, "HR administrator", "Manages people records and users, and can see sensitive fields.", true,
        [
            Permissions.Identity.UserView, Permissions.Identity.UserManage, Permissions.Identity.RoleView,
            Permissions.Identity.SensitiveView, Permissions.Core.LegalEntityView, Permissions.Core.LegalEntityManage,
        ]),
        new(PayrollAdmin, "Payroll administrator", "Runs payroll and can see sensitive pay fields.", true,
        [
            Permissions.Identity.SensitiveView, Permissions.Core.LegalEntityView,
        ]),
        new(Manager, "Manager", "Sees and acts on their own team within the reporting chain.", false,
        [
            Permissions.Core.LegalEntityView,
        ]),
        new(Employee, "Employee", "Self service only.", false, []),
        new(Auditor, "Auditor", "Read-only access for audit and compliance.", false,
        [
            Permissions.Identity.UserView, Permissions.Identity.RoleView, Permissions.Core.LegalEntityView,
        ]),
    ];
}
