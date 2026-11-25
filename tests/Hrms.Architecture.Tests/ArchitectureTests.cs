using FluentAssertions;
using NetArchTest.Rules;

namespace Hrms.Architecture.Tests;

public class ArchitectureTests
{
    [Fact]
    public void ModuleDiscovery_FindsEveryModuleProjectOnDisk()
    {
        // Guards the guard: if a module's projects are not referenced by this test project, its rules would silently not run.
        SolutionModel.ModuleNames.Should().Contain("Core");
        SolutionModel.ModuleAssemblies.Count.Should().Be(SolutionModel.ModuleProjectCountOnDisk());
    }

    [Fact]
    public void Modules_DoNotDependOnOtherModulesDomainOrInfrastructure()
    {
        var violations = ModuleBoundaryRule.FindViolations(SolutionModel.ModuleAssemblies, SolutionModel.ModuleNames);

        violations.Should().BeEmpty();
    }

    [Fact]
    public void ModuleBoundaryRule_WhenModuleReachesIntoAnotherModule_ReportsDomainAndInfrastructureAccess()
    {
        var violations = ModuleBoundaryRule.FindViolations(
            [typeof(ArchitectureTests).Assembly],
            ["FixtureA", "FixtureB"]);

        violations.Should().HaveCount(2);
        violations.Should().Contain(v => v.Contains("BadDomainHandler", StringComparison.Ordinal));
        violations.Should().Contain(v => v.Contains("BadInfrastructureHandler", StringComparison.Ordinal));
    }

    [Fact]
    public void Domain_DoesNotDependOnOuterLayersOrFrameworks()
    {
        foreach (var module in SolutionModel.ModuleNames)
        {
            var result = Types.InAssembly(SolutionModel.Layer(module, "Domain"))
                .ShouldNot().HaveDependencyOnAny(
                    $"Hrms.{module}.Application.", $"Hrms.{module}.Infrastructure.", $"Hrms.{module}.Api.",
                    "Microsoft.AspNetCore", "Microsoft.EntityFrameworkCore", "Hrms.Persistence", "Hrms.Http")
                .GetResult();

            result.IsSuccessful.Should().BeTrue($"{module}.Domain: {string.Join(", ", result.FailingTypeNames ?? [])}");
        }
    }

    [Fact]
    public void Application_DoesNotDependOnInfrastructureOrApi()
    {
        foreach (var module in SolutionModel.ModuleNames)
        {
            var result = Types.InAssembly(SolutionModel.Layer(module, "Application"))
                .ShouldNot().HaveDependencyOnAny($"Hrms.{module}.Infrastructure.", $"Hrms.{module}.Api.", "Microsoft.AspNetCore", "Hrms.Http")
                .GetResult();

            result.IsSuccessful.Should().BeTrue($"{module}.Application: {string.Join(", ", result.FailingTypeNames ?? [])}");
        }
    }

    [Fact]
    public void ModulesAndPlatform_DoNotDependOnTheHosts()
    {
        var subjects = SolutionModel.ProductAssemblies
            .Where(a => SolutionModel.IsModuleAssemblyName(a.GetName().Name!) || SolutionModel.PlatformAssemblyNames.Contains(a.GetName().Name))
            .ToArray();

        var offenders = subjects
            .SelectMany(a => SolutionModel.ReferencedHrmsAssemblies(a).Select(r => (From: a.GetName().Name!, To: r)))
            .Where(edge => edge.To is "Hrms.Api" or "Hrms.Worker")
            .ToArray();

        offenders.Should().BeEmpty();
    }

    [Fact]
    public void Platform_DoesNotReferenceModules()
    {
        var offenders = SolutionModel.ProductAssemblies
            .Where(a => SolutionModel.PlatformAssemblyNames.Contains(a.GetName().Name))
            .SelectMany(a => SolutionModel.ReferencedHrmsAssemblies(a).Select(r => (From: a.GetName().Name!, To: r)))
            .Where(edge => SolutionModel.IsModuleAssemblyName(edge.To))
            .ToArray();

        offenders.Should().BeEmpty();
    }

    [Fact]
    public void Contracts_HasNoDependenciesOnOtherSolutionAssemblies()
    {
        var contracts = SolutionModel.ProductAssemblies.Single(a => a.GetName().Name == "Hrms.Contracts");

        SolutionModel.ReferencedHrmsAssemblies(contracts).Should().BeEmpty();
    }

    [Fact]
    public void SharedKernel_HasNoDependenciesOnOtherSolutionAssemblies()
    {
        var kernel = SolutionModel.ProductAssemblies.Single(a => a.GetName().Name == "Hrms.SharedKernel");

        SolutionModel.ReferencedHrmsAssemblies(kernel).Should().BeEmpty();
    }
}
