using System.Reflection;
using FluentAssertions;
using Hrms.Core.Infrastructure;
using Hrms.Core.Infrastructure.Persistence;
using Hrms.Employee.Infrastructure;
using Hrms.Employee.Infrastructure.Persistence;
using Hrms.Identity.Infrastructure;
using Hrms.Identity.Infrastructure.Persistence;
using Hrms.Persistence.Auditing;
using Hrms.SharedKernel;
using Hrms.Workflow.Infrastructure;
using Hrms.Workflow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Hrms.Architecture.Tests;

public class AuditRulesTests
{
    private const string Unused = "Server=127.0.0.1;Database=unused;User ID=x;Password=x";

    /// <summary>
    /// Every field the audit log records with its value, for every audited table. A new property fails this test until someone decides:
    /// list it here (values are kept for as long as the log is), or mark it [AuditSensitive] or [AuditIgnore].
    /// </summary>
    private static readonly Dictionary<string, string> RecordedInTheClear = new(StringComparer.Ordinal)
    {
        ["cost_centre"] = "Code IsActive LegalEntityId Name",
        ["delegation"] = "DelegateUserId DelegatorUserId EndsOn RevokedAt StartsOn WorkflowKey",
        ["designation"] = "Code GradeId IsActive Name",
        ["employee"] = "Code ConfirmationDate JoiningDate LastWorkingDay",
        ["employee_code_rule"] = "LegalEntityId Prefix Width",
        ["employee_consent"] = "ConsentType EmployeeId GrantedAt Source Version WithdrawnAt",
        ["employee_device"] = "DeviceId EmployeeId Name Platform RegisteredAt RevokedAt Status",
        ["employee_document"] = "DocumentType EmployeeId ExpiresOn IssuedOn Title",
        ["employee_job"] = "CostCentreId DesignationId EffectiveFrom EffectiveTo EmployeeId EmployeeTypeId GradeId LegalEntityId "
                           + "LocationId ManagerId NoticePeriodDays OrgUnitId ProbationEnd Reason",
        ["employee_personal"] = "EmployeeId FirstName Gender LastName MiddleName",
        ["employee_separation"] = "EmployeeId LastWorkingDay NoticeDate Reason Type",
        ["employee_statutory_id"] = "EmployeeId IdType",
        ["employee_type"] = "Code IsActive IsPayrollEligible Name",
        ["geofence"] = "CenterLatitude CenterLongitude IsActive Kind LocationId Name PolygonGeoJson RadiusMetres",
        ["grade"] = "Code IsActive Name Rank",
        ["legal_entity"] = "Code CountryCode CountryPackCode CurrencyCode DefaultLanguage FiscalYearStartMonth Name TimeZoneId",
        ["location"] = "AddressLine1 AddressLine2 City Code CountryCode HolidayCalendarId IsActive Name PostalCode State TimeZoneId "
                       + "WorkWeekDays",
        ["org_unit"] = "Code IsActive Name OrgUnitTypeId",
        ["org_unit_head"] = "EffectiveFrom EffectiveTo EmployeeId OrgUnitId",
        ["org_unit_parent"] = "EffectiveFrom EffectiveTo OrgUnitId ParentId",
        ["org_unit_type"] = "Code IsActive Level Name",
        ["role"] = "Code Description IsSystem Name RequiresMfa",
        ["role_permission"] = "PermissionCode RoleId",
        ["tenant_security_policy"] = "EffectiveFrom EffectiveTo LockoutMinutes MaxFailedAttempts MinPasswordLength RequireDigit "
                                     + "RequireLowercase RequireSymbol RequireUppercase",
        ["user_account"] = "DisplayName Email EmployeeId MfaEnabled MfaEnrolledAt MustChangePassword PasswordChangedAt Status",
        ["user_role_scope"] = "RoleId ScopeType ScopeValue UserId",
        ["workflow_def"] = "EffectiveFrom Name PublishedAt Status Version WorkflowKey",
        ["workflow_stage_def"] = "ApproverJson ConditionJson DefinitionId EscalationJson FallbackJson Mode Name OnTimeout SlaHours StageOrder",
    };

    private static readonly HashSet<string> Bookkeeping =
        ["Id", "TenantId", "CreatedAt", "CreatedBy", "UpdatedAt", "UpdatedBy", "IsDeleted", "RowVersion"];

    private static DbContextOptions<T> Options<T>(Func<DbContextOptionsBuilder, string, DbContextOptionsBuilder> use)
        where T : DbContext => (DbContextOptions<T>)use(new DbContextOptionsBuilder<T>(), Unused).Options;

    private static List<DbContext> Contexts()
    {
        var tenant = new TenantContext();
        var actor = new SystemActorContext();
        return
        [
            new CoreDbContext(Options<CoreDbContext>(CoreInfrastructureExtensions.UseCoreMySql), tenant, actor, TimeProvider.System),
            new IdentityDbContext(
                Options<IdentityDbContext>(IdentityInfrastructureExtensions.UseIdentityMySql), tenant, actor, TimeProvider.System),
            new EmployeeDbContext(
                Options<EmployeeDbContext>(EmployeeInfrastructureExtensions.UseEmployeeMySql), tenant, actor, TimeProvider.System),
            new WorkflowDbContext(
                Options<WorkflowDbContext>(WorkflowInfrastructureExtensions.UseWorkflowMySql), tenant, actor, TimeProvider.System),
        ];
    }

    private static IEnumerable<IEntityType> Audited(IModel model) =>
        model.GetEntityTypes().Where(e => e.ClrType.GetCustomAttribute<AuditedAttribute>(inherit: true) is not null);

    [Fact]
    public void AuditedEntities_RecordInTheClearOnlyTheListedFields()
    {
        var actual = Contexts().SelectMany(c => Audited(c.Model)).ToDictionary(
            entity => entity.GetTableName()!,
            entity => string.Join(' ', entity.GetProperties()
                .Where(p => p.PropertyInfo is { } member
                            && !Bookkeeping.Contains(p.Name)
                            && !member.IsDefined(typeof(AuditSensitiveAttribute), inherit: true)
                            && !member.IsDefined(typeof(AuditIgnoreAttribute), inherit: true))
                .Select(p => p.Name)
                .Order(StringComparer.Ordinal)),
            StringComparer.Ordinal);

        actual.Should().BeEquivalentTo(RecordedInTheClear);
    }

    [Fact]
    public void AuditRoots_NameAnAuditedTableAndAnIdProperty()
    {
        var entities = Contexts().SelectMany(c => Audited(c.Model)).ToList();
        var tables = entities.Select(e => e.GetTableName()).ToHashSet(StringComparer.Ordinal);

        foreach (var entity in entities)
        {
            var audited = entity.ClrType.GetCustomAttribute<AuditedAttribute>(inherit: true)!;
            if (audited.RootType is null && audited.RootKey is null)
            {
                continue;
            }

            tables.Should().Contain(audited.RootType, "the root of {0} must be an audited table", entity.DisplayName());
            entity.FindProperty(audited.RootKey!)!.ClrType.Should().Be<Guid>();
        }
    }

    [Fact]
    public void ContextsWithAuditedEntities_FilterTheAuditLogByTenantAndLeaveItOutOfTheirMigrations()
    {
        foreach (var context in Contexts())
        {
            var audit = context.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(AuditEntry))!;

            audit.GetQueryFilter().Should().NotBeNull();
            audit.IsTableExcludedFromMigrations().Should().BeTrue("only Hrms.Audit creates the table");
        }
    }
}
