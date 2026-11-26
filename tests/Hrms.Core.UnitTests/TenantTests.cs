using FluentAssertions;
using Hrms.Core.Domain.Tenants;
using Hrms.SharedKernel;

namespace Hrms.Core.UnitTests;

public class TenantTests
{
    private static readonly DateTime Now = new(2026, 11, 25, 10, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("acme", "ACME")]
    [InlineData("  Acme-Corp  ", "ACME-CORP")]
    [InlineData("a1", "A1")]
    public void Create_WithValidCode_NormalisesToUpperCase(string code, string expected)
    {
        var result = Tenant.Create(code, "Acme Corporation", Now);

        result.IsSuccess.Should().BeTrue();
        result.Value.Code.Should().Be(expected);
        result.Value.Status.Should().Be(TenantStatus.Active);
        result.Value.CreatedAt.Should().Be(Now);
        result.Value.TenantId.Value.Should().Be(result.Value.Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("-acme")]
    [InlineData("acme corp")]
    [InlineData("acme_corp")]
    [InlineData("acme!")]
    [InlineData("this-code-is-way-longer-than-thirty-two-characters")]
    public void Create_WithInvalidCode_FailsWithAStableCode(string? code)
    {
        var result = Tenant.Create(code, "Acme", Now);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("core.tenant.code_invalid");
        result.Error.Type.Should().Be(ErrorType.Validation);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithoutAName_Fails(string? name) =>
        Tenant.Create("ACME", name, Now).Error.Code.Should().Be("core.tenant.name_invalid");

    [Fact]
    public void Create_WithANameOver200Characters_Fails() =>
        Tenant.Create("ACME", new string('x', 201), Now).Error.Code.Should().Be("core.tenant.name_invalid");

    [Fact]
    public void Create_WithANonUtcCreationTime_Throws() =>
        FluentActions.Invoking(() => Tenant.Create("ACME", "Acme", DateTime.Now))
            .Should().Throw<ArgumentException>();

    [Fact]
    public void SuspendAndActivate_ChangeTheStatus()
    {
        var tenant = Tenant.Create("ACME", "Acme", Now).Value;

        tenant.Suspend();
        tenant.Status.Should().Be(TenantStatus.Suspended);

        tenant.Activate();
        tenant.Status.Should().Be(TenantStatus.Active);
    }
}
