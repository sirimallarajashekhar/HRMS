using FluentAssertions;
using Hrms.Core.Domain.Org;

namespace Hrms.Core.UnitTests;

public class OrgMasterTests
{
    // --- the shared code and name rules, exercised through each master --------------------------------------------------

    public static TheoryData<string, Func<string?, string?, Hrms.SharedKernel.Result>> Factories => new()
    {
        { "org_unit_type", (c, n) => OrgUnitType.Create(c, n, 1) },
        { "org_unit", (c, n) => OrgUnit.Create(c, n, Guid.NewGuid()) },
        { "grade", (c, n) => Grade.Create(c, n, 1) },
        { "designation", (c, n) => Designation.Create(c, n, null) },
        { "cost_centre", (c, n) => CostCentre.Create(c, n, null) },
        { "employee_type", (c, n) => EmployeeType.Create(c, n, true) },
    };

    [Theory]
    [MemberData(nameof(Factories))]
    public void Create_WithABadCode_FailsWithThatMastersCode(string master, Func<string?, string?, Hrms.SharedKernel.Result> create)
    {
        foreach (var bad in new string?[] { null, "", "  ", "-X", "has space", "ünï", new string('A', 33) })
        {
            var result = create(bad, "Name");

            result.IsFailure.Should().BeTrue($"'{bad}'");
            result.Error.Code.Should().Be($"core.{master}.code_invalid");
        }
    }

    [Theory]
    [MemberData(nameof(Factories))]
    public void Create_WithABadName_FailsWithThatMastersCode(string master, Func<string?, string?, Hrms.SharedKernel.Result> create)
    {
        foreach (var bad in new string?[] { null, "", "   ", new string('x', 201) })
        {
            var result = create("OK1", bad);

            result.IsFailure.Should().BeTrue();
            result.Error.Code.Should().Be($"core.{master}.name_invalid");
        }
    }

    [Theory]
    [MemberData(nameof(Factories))]
    public void Create_WithAGoodCodeAndName_Succeeds(string master, Func<string?, string?, Hrms.SharedKernel.Result> create)
    {
        _ = master;

        create("A", "Name").IsSuccess.Should().BeTrue();
        create(new string('A', 32), new string('n', 200)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Create_NormalisesTheCodeAndTrimsTheName()
    {
        var grade = Grade.Create("  g-1 ", "  Senior  ", 5).Value;

        grade.Code.Should().Be("G-1");
        grade.Name.Should().Be("Senior");
        grade.IsActive.Should().BeTrue();
    }

    [Fact]
    public void Update_WithABadName_ChangesNothing()
    {
        var grade = Grade.Create("G1", "Senior", 5).Value;

        grade.Update(" ", 9, isActive: false).IsFailure.Should().BeTrue();

        (grade.Name, grade.Rank, grade.IsActive).Should().Be(("Senior", 5, true));
    }

    [Fact]
    public void Update_NeverChangesTheCode()
    {
        var unit = OrgUnit.Create("ENG", "Engineering", Guid.NewGuid()).Value;

        unit.Update("Engineering and Product", isActive: false).IsSuccess.Should().BeTrue();

        (unit.Code, unit.Name, unit.IsActive).Should().Be(("ENG", "Engineering and Product", false));
    }

    // --- master specific fields -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    [InlineData(-1)]
    public void OrgUnitType_WithALevelOutOfRange_Fails(int level)
    {
        OrgUnitType.Create("DEPT", "Department", level).Error.Code.Should().Be("core.org_unit_type.level_invalid");

        var type = OrgUnitType.Create("DEPT", "Department", 2).Value;
        type.Update("Department", level, true).Error.Code.Should().Be("core.org_unit_type.level_invalid");
        type.Level.Should().Be(2);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10_001)]
    public void Grade_WithARankOutOfRange_Fails(int rank)
    {
        Grade.Create("G1", "Grade", rank).Error.Code.Should().Be("core.grade.rank_invalid");
    }

    [Fact]
    public void Designation_CanBeLinkedToAGradeAndUnlinked()
    {
        var grade = Guid.NewGuid();
        var designation = Designation.Create("SE", "Software Engineer", grade).Value;

        designation.GradeId.Should().Be(grade);
        designation.Update("Software Engineer", null, true).IsSuccess.Should().BeTrue();
        designation.GradeId.Should().BeNull();
    }

    [Fact]
    public void Update_WhenTheNameIsInvalid_DoesNotChangeTheLink()
    {
        var entity = Guid.NewGuid();
        var centre = CostCentre.Create("CC1", "Delivery", entity).Value;

        centre.Update("", null, true).IsFailure.Should().BeTrue();

        centre.LegalEntityId.Should().Be(entity);
    }

    [Fact]
    public void EmployeeType_KeepsThePayrollFlag()
    {
        var type = EmployeeType.Create("INTERN", "Intern", isPayrollEligible: false).Value;

        type.IsPayrollEligible.Should().BeFalse();
        type.Update("Intern", true, true).IsSuccess.Should().BeTrue();
        type.IsPayrollEligible.Should().BeTrue();
    }

    [Fact]
    public void OrgUnitParent_StartsOpenEndedAndCanBeClosed()
    {
        var link = OrgUnitParent.Create(Guid.NewGuid(), null, new DateOnly(2026, 1, 1));

        link.EffectiveTo.Should().BeNull();
        link.IsEffectiveOn(new DateOnly(2030, 1, 1)).Should().BeTrue();

        link.EndOn(new DateOnly(2026, 5, 31));
        link.IsEffectiveOn(new DateOnly(2026, 6, 1)).Should().BeFalse();
    }
}
