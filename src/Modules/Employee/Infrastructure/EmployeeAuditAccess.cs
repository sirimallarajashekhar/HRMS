using Hrms.Contracts;

namespace Hrms.Employee.Infrastructure;

/// <summary>
/// Each part of an employee's history needs what the profile needs to show that part, within the data scope of that permission: someone
/// who may see the employee but not their personal data sees no personal changes. Separation details sit with personal data, as on the
/// profile. So do the reasons given for changes: one may explain a change to personal data or a separation.
/// </summary>
internal sealed class EmployeeAuditAccess(EmployeeAccess access) : IAuditRecordAccess
{
    private const string EmployeeType = "employee";
    private const string CodeRuleType = "employee_code_rule";

    public IReadOnlyList<AuditRecordType> RecordTypes { get; } =
    [
        new(EmployeeType,
        [
            new(EmployeeType, Permissions.Employee.EmployeeView),
            new("employee_job", Permissions.Employee.EmployeeView),
            new("employee_personal", Permissions.Employee.PersonalView),
            new("employee_separation", Permissions.Employee.PersonalView),
            new("employee_statutory_id", Permissions.Employee.StatutoryIdView),
            new("employee_document", Permissions.Employee.DocumentView),
            new("employee_device", Permissions.Employee.DeviceView),
            new("employee_consent", Permissions.Employee.ConsentView),
        ],
        ReasonPermission: Permissions.Employee.PersonalView),
        new(CodeRuleType, [new(CodeRuleType, Permissions.Employee.CodeRuleManage)]),
    ];

    public async Task<bool> IsInScopeAsync(string rootType, Guid recordId, string permission, CancellationToken cancellationToken) =>
        rootType switch
        {
            EmployeeType => (await access.LoadAsync(recordId, permission, tracked: false, cancellationToken)).IsSuccess,
            CodeRuleType => true, // tenant-wide configuration, as on its own endpoints
            _ => false,
        };
}
