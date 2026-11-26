using FluentAssertions;
using Hrms.Contracts;
using Hrms.Identity.Domain.Roles;
using Hrms.Identity.Domain.Security;
using Hrms.Identity.Domain.Users;
using Hrms.SharedKernel;

namespace Hrms.Identity.UnitTests;

public class PolicyAndRoleTests
{
    // --- password policy -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("short", new string[] { PasswordPolicy.TooShort })]
    [InlineData("a-long-enough-pass", new string[0])]
    [InlineData(null, new string[] { PasswordPolicy.TooShort })]
    [InlineData("", new string[] { PasswordPolicy.TooShort })]
    public void DefaultPolicy_AsksForLengthOnly(string? password, string[] expected) =>
        PasswordPolicy.Default.Validate(password).Should().Equal(expected);

    [Fact]
    public void Policy_WithEveryRule_ReportsEachViolationOnce()
    {
        var policy = new PasswordPolicy(10, true, true, true, true);

        policy.Validate("abc").Should().Equal(
            PasswordPolicy.TooShort, PasswordPolicy.MissingUppercase, PasswordPolicy.MissingDigit, PasswordPolicy.MissingSymbol);
        policy.Validate("ABCDEFGHIJ").Should().Equal(PasswordPolicy.MissingLowercase, PasswordPolicy.MissingDigit, PasswordPolicy.MissingSymbol);
        policy.Validate("Abcdefgh1!").Should().BeEmpty();
    }

    [Fact]
    public void Policy_TreatsWhitespaceAsNeitherASymbolNorADigit()
    {
        var policy = new PasswordPolicy(8, false, false, false, true);

        policy.Validate("abc def gh").Should().Contain(PasswordPolicy.MissingSymbol);
    }

    [Fact]
    public void Policy_RejectsAPasswordOverTheMaximumSoItCannotBurnHasherCpu()
    {
        var tooLong = new string('a', PasswordPolicy.MaxLength + 1);

        PasswordPolicy.Default.Validate(tooLong).Should().Equal(PasswordPolicy.TooLong);
        PasswordPolicy.Default.Validate(new string('a', PasswordPolicy.MaxLength)).Should().BeEmpty();
    }

    // --- tenant security policy ------------------------------------------------------------------------------------

    private static readonly DateOnly Today = new(2026, 11, 26);

    [Fact]
    public void SecurityPolicy_WithValidValues_MapsToThePasswordAndLockoutPolicies()
    {
        var policy = TenantSecurityPolicy.Create(Today, 14, true, false, true, false, 4, 30).Value;

        policy.ToPasswordPolicy().Should().Be(new PasswordPolicy(14, true, false, true, false));
        policy.ToLockoutPolicy().Should().Be(new LockoutPolicy(4, TimeSpan.FromMinutes(30)));
        policy.EffectiveFrom.Should().Be(Today);
        policy.EffectiveTo.Should().BeNull();
    }

    [Theory]
    [InlineData(7, 5, 15, "min_length_invalid")]
    [InlineData(129, 5, 15, "min_length_invalid")]
    [InlineData(12, 2, 15, "max_failed_attempts_invalid")]
    [InlineData(12, 21, 15, "max_failed_attempts_invalid")]
    [InlineData(12, 5, 0, "lockout_minutes_invalid")]
    [InlineData(12, 5, 1441, "lockout_minutes_invalid")]
    public void SecurityPolicy_OutsideTheAllowedBounds_FailsWithTheFieldCode(int minLength, int attempts, int minutes, string code)
    {
        var result = TenantSecurityPolicy.Create(Today, minLength, false, false, false, false, attempts, minutes);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be($"identity.security_policy.{code}");
    }

    [Theory]
    [InlineData(8, 3, 1)]
    [InlineData(128, 20, 1440)]
    public void SecurityPolicy_AtTheBounds_IsAccepted(int minLength, int attempts, int minutes) =>
        TenantSecurityPolicy.Create(Today, minLength, false, false, false, false, attempts, minutes).IsSuccess.Should().BeTrue();

    [Fact]
    public void SecurityPolicy_IsEffectiveDated_SoAChangeClosesTheOldRowAndStartsANewOne()
    {
        var old = TenantSecurityPolicy.Create(new DateOnly(2026, 1, 1), 12, false, false, false, false, 5, 15).Value;
        var next = TenantSecurityPolicy.Create(new DateOnly(2026, 7, 1), 14, true, true, true, false, 5, 15).Value;

        old.EndOn(new DateOnly(2026, 6, 30));

        new[] { old, next }.AsQueryable().ValidOn(new DateOnly(2026, 6, 30)).Should().ContainSingle().Which.Should().BeSameAs(old);
        new[] { old, next }.AsQueryable().ValidOn(new DateOnly(2026, 7, 1)).Should().ContainSingle().Which.Should().BeSameAs(next);
    }

    // --- roles -----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("HR_Admin", "hr_admin")]
    [InlineData("  payroll_admin ", "payroll_admin")]
    public void Role_NormalisesTheCode(string input, string expected) =>
        Role.Create(input, "Name", null, false, false).Value.Code.Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("1admin")]
    [InlineData("has space")]
    [InlineData("hr-admin")]
    public void Role_WithAnInvalidCode_Fails(string? code) =>
        Role.Create(code, "Name", null, false, false).Error.Code.Should().Be("identity.role.code_invalid");

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void Role_WithoutAName_Fails(string? name) =>
        Role.Create("auditor", name, null, false, false).Error.Code.Should().Be("identity.role.name_invalid");

    [Fact]
    public void Role_WithAnOverlongDescription_Fails() =>
        Role.Create("auditor", "Auditor", new string('x', 501), false, false).Error.Code.Should().Be("identity.role.description_invalid");

    [Fact]
    public void SystemRole_CannotStopRequiringMfa_ButCanBeRenamed()
    {
        var role = Role.Create("tenant_admin", "Tenant admin", null, isSystem: true, requiresMfa: true).Value;

        role.Update("Tenant administrator", null, requiresMfa: false).Error.Code.Should().Be("identity.role.system_mfa_required");
        role.RequiresMfa.Should().BeTrue();
        role.Name.Should().Be("Tenant admin");

        role.Update("Tenant administrator", "Renamed", requiresMfa: true).IsSuccess.Should().BeTrue();
        role.Name.Should().Be("Tenant administrator");
    }

    [Fact]
    public void CustomRole_CanSwitchMfaOnAndOff()
    {
        var role = Role.Create("custom", "Custom", null, isSystem: false, requiresMfa: false).Value;

        role.Update("Custom", null, requiresMfa: true).IsSuccess.Should().BeTrue();
        role.Update("Custom", null, requiresMfa: false).IsSuccess.Should().BeTrue();
        role.RequiresMfa.Should().BeFalse();
    }

    [Fact]
    public void RoleGrant_ForAnUnknownPermission_Fails() =>
        RoleGrant.Create(Guid.CreateVersion7(), "core.legal_entity.vieww").Error.Code.Should().Be("identity.role_permission.unknown_permission");

    [Fact]
    public void RoleGrant_ForACataloguedPermission_Succeeds()
    {
        var roleId = Guid.CreateVersion7();

        var grant = RoleGrant.Create(roleId, Permissions.Core.LegalEntityView).Value;

        grant.RoleId.Should().Be(roleId);
        grant.PermissionCode.Should().Be(Permissions.Core.LegalEntityView);
    }

    // --- scopes ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(ScopeType.Self)]
    [InlineData(ScopeType.DirectReports)]
    [InlineData(ScopeType.AllReports)]
    [InlineData(ScopeType.Tenant)]
    public void Scope_WithoutAValue_IsValidForScopesThatNeedNone(ScopeType type)
    {
        var scope = UserRoleScope.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), type, null).Value;

        scope.ScopeValue.Should().Be(Guid.Empty);
        UserRoleScope.NeedsValue(type).Should().BeFalse();
    }

    [Theory]
    [InlineData(ScopeType.Department)]
    [InlineData(ScopeType.Location)]
    [InlineData(ScopeType.LegalEntity)]
    public void Scope_ThatPointsAtOneThing_NeedsItsId(ScopeType type)
    {
        var withoutValue = UserRoleScope.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), type, null);
        var emptyValue = UserRoleScope.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), type, Guid.Empty);
        var target = Guid.CreateVersion7();
        var withValue = UserRoleScope.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), type, target);

        withoutValue.Error.Code.Should().Be("identity.user_role_scope.value_required");
        emptyValue.Error.Code.Should().Be("identity.user_role_scope.value_required");
        withValue.Value.ScopeValue.Should().Be(target);
    }

    [Theory]
    [InlineData(ScopeType.Self)]
    [InlineData(ScopeType.DirectReports)]
    [InlineData(ScopeType.AllReports)]
    [InlineData(ScopeType.Tenant)]
    public void Scope_ThatNeedsNoValue_RejectsOne(ScopeType type) =>
        UserRoleScope.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), type, Guid.CreateVersion7())
            .Error.Code.Should().Be("identity.user_role_scope.value_not_allowed");

    [Fact]
    public void Scope_WithAnUndefinedType_Fails() =>
        UserRoleScope.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), (ScopeType)99, null)
            .Error.Code.Should().Be("identity.user_role_scope.scope_type_invalid");

    // --- default roles ---------------------------------------------------------------------------------------------

    [Fact]
    public void DefaultRoles_AreTheSixApprovedRolesWithUniqueCodes() =>
        DefaultRoles.All.Select(r => r.Code).Should().Equal(
            "tenant_admin", "hr_admin", "payroll_admin", "manager", "employee", "auditor");

    [Fact]
    public void DefaultRoles_OnlyGrantCataloguedPermissions() =>
        DefaultRoles.All.SelectMany(r => r.PermissionCodes).Where(code => !Permissions.IsKnown(code)).Should().BeEmpty();

    [Fact]
    public void DefaultRoles_TenantAdminHasEveryPermission() =>
        DefaultRoles.All.Single(r => r.Code == DefaultRoles.TenantAdmin).PermissionCodes.Should().BeEquivalentTo(Permissions.All);

    [Fact]
    public void DefaultRoles_TheThreeAdministratorRolesRequireMfaAndTheOthersDoNot()
    {
        DefaultRoles.All.Where(r => r.RequiresMfa).Select(r => r.Code)
            .Should().BeEquivalentTo(DefaultRoles.TenantAdmin, DefaultRoles.HrAdmin, DefaultRoles.PayrollAdmin);
    }

    [Fact]
    public void DefaultRoles_EmployeeHasNoPermissions_AndOnlyAdminsCanSeeSensitiveFields()
    {
        DefaultRoles.All.Single(r => r.Code == DefaultRoles.Employee).PermissionCodes.Should().BeEmpty();
        DefaultRoles.All.Where(r => r.PermissionCodes.Contains(Permissions.Identity.SensitiveView)).Select(r => r.Code)
            .Should().BeEquivalentTo(DefaultRoles.TenantAdmin, DefaultRoles.HrAdmin, DefaultRoles.PayrollAdmin);
    }

    [Fact]
    public void DefaultRoles_AreAllValidRolesAndGrants()
    {
        foreach (var definition in DefaultRoles.All)
        {
            var role = Role.Create(definition.Code, definition.Name, definition.Description, isSystem: true, definition.RequiresMfa);
            role.IsSuccess.Should().BeTrue(definition.Code);
            definition.PermissionCodes.All(code => RoleGrant.Create(role.Value.Id, code).IsSuccess).Should().BeTrue(definition.Code);
        }
    }

    [Fact]
    public void ErrorTypes_OfDomainFailures_AreValidationExceptForTheSystemRoleRule()
    {
        Role.Create("x", "n", null, false, false).Error.Type.Should().Be(ErrorType.Validation);
        Role.Create("tenant_admin", "n", null, true, true).Value.Update("n", null, false).Error.Type.Should().Be(ErrorType.Forbidden);
    }
}
