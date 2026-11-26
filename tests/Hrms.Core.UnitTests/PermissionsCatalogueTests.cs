using System.Text.RegularExpressions;
using FluentAssertions;
using Hrms.Contracts;

namespace Hrms.Core.UnitTests;

public partial class PermissionsCatalogueTests
{
    [GeneratedRegex("^[a-z][a-z0-9_]*\\.[a-z][a-z0-9_]*\\.[a-z][a-z0-9_]*$")]
    private static partial Regex CodeFormat();

    [Fact]
    public void Catalogue_IsNotEmpty() => Permissions.All.Should().NotBeEmpty();

    [Fact]
    public void EveryCode_IsModuleDotEntityDotActionInLowerSnakeCase() =>
        Permissions.All.Where(code => !CodeFormat().IsMatch(code)).Should().BeEmpty();

    [Fact]
    public void EveryCode_StartsWithItsModulesName()
    {
        foreach (var module in typeof(Permissions).GetNestedTypes())
        {
            var prefix = module.Name.ToLowerInvariant() + ".";
            var codes = module.GetFields().Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!);

            codes.Where(code => !code.StartsWith(prefix, StringComparison.Ordinal)).Should().BeEmpty($"codes in {module.Name} start with '{prefix}'");
        }
    }

    [Fact]
    public void Codes_AreUniqueAcrossModules()
    {
        var all = typeof(Permissions).GetNestedTypes()
            .SelectMany(m => m.GetFields().Where(f => f.IsLiteral))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToArray();

        all.Should().OnlyHaveUniqueItems();
        Permissions.All.Should().HaveCount(all.Length);
    }

    [Theory]
    [InlineData("identity.user.view", true)]
    [InlineData("core.legal_entity.manage", true)]
    [InlineData("core.legal_entity.vieww", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("IDENTITY.USER.VIEW", false)]
    public void IsKnown_OnlyAcceptsCataloguedCodesExactly(string? code, bool expected) =>
        Permissions.IsKnown(code).Should().Be(expected);

    [Fact]
    public void SensitiveView_IsInTheCatalogue() => Permissions.IsKnown(Permissions.Identity.SensitiveView).Should().BeTrue();
}
