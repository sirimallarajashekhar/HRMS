using System.Reflection;
using FluentAssertions;
using Hrms.Contracts;
using Hrms.Core.Infrastructure;
using Hrms.Employee.Infrastructure;
using Hrms.Identity.Infrastructure;
using Hrms.SharedKernel;
using Hrms.Workflow.Infrastructure;

namespace Hrms.Architecture.Tests;

/// <summary>Every audited record can be read back through the history API by someone, and by exactly one rule.</summary>
public class AuditAccessRulesTests
{
    private static readonly Assembly[] Infrastructure =
    [
        typeof(CoreInfrastructureExtensions).Assembly,
        typeof(EmployeeInfrastructureExtensions).Assembly,
        typeof(IdentityInfrastructureExtensions).Assembly,
        typeof(WorkflowInfrastructureExtensions).Assembly,
    ];

    private static List<AuditRecordType> RecordTypes() => Infrastructure
        .SelectMany(assembly => assembly.GetTypes())
        .Where(type => type is { IsClass: true, IsAbstract: false } && type.IsAssignableTo(typeof(IAuditRecordAccess)))
        .SelectMany(type =>
        {
            // The rules are fixed data, so the constructor's services are not needed to read them.
            var constructor = type.GetConstructors().Single();
            var access = (IAuditRecordAccess)constructor.Invoke(new object?[constructor.GetParameters().Length]);
            return access.RecordTypes;
        })
        .ToList();

    /// <summary>(root table, table) for every audited entity of every module.</summary>
    private static List<(string Root, string Entity)> AuditedTables() => AuditRulesTests.AuditedEntityTables();

    [Fact]
    public void EveryAuditedTable_HasExactlyOneViewRuleUnderItsRoot()
    {
        var rules = RecordTypes().SelectMany(t => t.Rules.Select(r => (Root: t.RootType, Entity: r.EntityType))).ToList();

        rules.Should().BeEquivalentTo(AuditedTables());
    }

    [Fact]
    public void RecordTypes_AreUniqueAndEachHasARuleForItsOwnRecord()
    {
        var types = RecordTypes();

        types.Select(t => t.RootType).Should().OnlyHaveUniqueItems();
        types.Should().AllSatisfy(t => t.Rules.Should().ContainSingle(r => r.EntityType == t.RootType));
    }

    [Fact]
    public void ViewRules_UseOnlyCataloguedPermissions() =>
        RecordTypes().SelectMany(t => t.Rules).Should().AllSatisfy(r => Permissions.IsKnown(r.Permission).Should().BeTrue(r.Permission));

    [Fact]
    public void EmployeeChangeReasons_NeedThePersonalDataPermission()
    {
        var types = RecordTypes();

        types.Single(t => t.RootType == "employee").ReasonPermission.Should().Be(Permissions.Employee.PersonalView);
        types.Where(t => t.ReasonPermission is not null)
            .Should().AllSatisfy(t => Permissions.IsKnown(t.ReasonPermission!).Should().BeTrue(t.ReasonPermission));
    }
}
