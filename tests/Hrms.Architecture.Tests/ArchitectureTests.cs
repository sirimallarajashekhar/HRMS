using System.Reflection;
using FluentAssertions;
using NetArchTest.Rules;

namespace Hrms.Architecture.Tests;

public class ArchitectureTests
{
    private static readonly Assembly SharedKernel = typeof(Hrms.SharedKernel.Error).Assembly;
    private static readonly Assembly CoreDomain = Hrms.Core.Domain.AssemblyReference.Assembly;
    private static readonly Assembly CoreApplication = Hrms.Core.Application.AssemblyReference.Assembly;
    private static readonly Assembly CoreInfrastructure = Hrms.Core.Infrastructure.AssemblyReference.Assembly;
    private static readonly Assembly CoreApi = Hrms.Core.Api.AssemblyReference.Assembly;

    // Add each new module's name here when its projects are created.
    private static readonly string[] Modules = ["Core"];

    [Fact]
    public void RealModules_DoNotDependOnOtherModulesDomainOrInfrastructure()
    {
        var violations = ModuleBoundaryRule.FindViolations(
            [CoreDomain, CoreApplication, CoreInfrastructure, CoreApi],
            Modules);

        violations.Should().BeEmpty();
    }

    [Fact]
    public void ModuleBoundaryRule_WhenModuleReachesIntoAnotherModulesDomain_ReportsViolation()
    {
        var violations = ModuleBoundaryRule.FindViolations(
            [typeof(ArchitectureTests).Assembly],
            ["FixtureA", "FixtureB"]);

        violations.Should().ContainSingle().Which.Should().Contain("BadHandler");
    }

    [Fact]
    public void Domain_DoesNotDependOnOuterLayersOrFrameworks()
    {
        var result = Types.InAssembly(CoreDomain)
            .ShouldNot().HaveDependencyOnAny(
                "Hrms.Core.Application", "Hrms.Core.Infrastructure", "Hrms.Core.Api",
                "Microsoft.AspNetCore", "Microsoft.EntityFrameworkCore")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Application_DoesNotDependOnInfrastructureOrApi()
    {
        var result = Types.InAssembly(CoreApplication)
            .ShouldNot().HaveDependencyOnAny("Hrms.Core.Infrastructure", "Hrms.Core.Api", "Microsoft.AspNetCore")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void SharedKernel_DoesNotDependOnModulesOrPlatform()
    {
        var result = Types.InAssembly(SharedKernel)
            .ShouldNot().HaveDependencyOnAny("Hrms.Core", "Hrms.Workflow", "Hrms.Policy", "Hrms.Notifications", "Hrms.Api")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(string.Join(", ", result.FailingTypeNames ?? []));
    }
}
