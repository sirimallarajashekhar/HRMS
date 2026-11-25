using System.Reflection;
using NetArchTest.Rules;

namespace Hrms.Architecture.Tests;

/// <summary>
/// A module (namespace Hrms.&lt;Module&gt;.*) may not depend on another module's Domain or Infrastructure.
/// Cross-module communication goes through Hrms.Contracts or domain events.
/// </summary>
public static class ModuleBoundaryRule
{
    public static IReadOnlyList<string> FindViolations(IEnumerable<Assembly> assemblies, IReadOnlyCollection<string> modules)
    {
        var assemblyList = assemblies.ToArray();
        var violations = new List<string>();

        foreach (var module in modules)
        {
            // NetArchTest matches namespaces by prefix, so layer sub-namespaces must be nested (Hrms.X.Domain.Events),
            // never siblings that merely start with the layer name (Hrms.X.DomainEvents).
            var forbidden = modules
                .Where(other => other != module)
                .SelectMany(other => new[] { $"Hrms.{other}.Domain", $"Hrms.{other}.Infrastructure" })
                .ToArray();
            if (forbidden.Length == 0)
            {
                continue;
            }

            var result = Types.InAssemblies(assemblyList)
                .That().ResideInNamespaceStartingWith($"Hrms.{module}.")
                .ShouldNot().HaveDependencyOnAny(forbidden)
                .GetResult();

            if (!result.IsSuccessful)
            {
                violations.AddRange((result.FailingTypeNames ?? []).Select(t => $"{t} (module {module})"));
            }
        }

        return violations;
    }
}
