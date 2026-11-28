using System.Reflection;
using System.Text.RegularExpressions;

namespace Hrms.Architecture.Tests;

/// <summary>
/// Discovers the solution's assemblies by naming convention so new modules are guarded automatically.
/// Module projects are Hrms.&lt;Module&gt;.(Domain|Application|Infrastructure|Api).
/// </summary>
public static partial class SolutionModel
{
    public static readonly string[] PlatformAssemblyNames = ["Hrms.Policy", "Hrms.Notifications", "Hrms.Persistence", "Hrms.Http", "Hrms.Logging", "Hrms.Audit"];

    [GeneratedRegex(@"^Hrms\.(?<module>[A-Za-z0-9]+)\.(?<layer>Domain|Application|Infrastructure|Api)$")]
    private static partial Regex ModuleAssemblyPattern();

    public static IReadOnlyList<Assembly> ProductAssemblies { get; } = Directory
        .GetFiles(AppContext.BaseDirectory, "Hrms.*.dll")
        .Where(path => !Path.GetFileNameWithoutExtension(path).EndsWith("Tests", StringComparison.Ordinal)
                       && !Path.GetFileNameWithoutExtension(path).EndsWith("UnitTests", StringComparison.Ordinal))
        .Select(Assembly.LoadFrom)
        .ToArray();

    public static IReadOnlyList<Assembly> ModuleAssemblies { get; } =
        ProductAssemblies.Where(a => ModuleAssemblyPattern().IsMatch(a.GetName().Name!)).ToArray();

    public static IReadOnlyList<string> ModuleNames { get; } = ModuleAssemblies
        .Select(a => ModuleAssemblyPattern().Match(a.GetName().Name!).Groups["module"].Value)
        .Distinct()
        .Order()
        .ToArray();

    public static Assembly Layer(string module, string layer) =>
        ModuleAssemblies.Single(a => a.GetName().Name == $"Hrms.{module}.{layer}");

    public static IEnumerable<string> ReferencedHrmsAssemblies(Assembly assembly) => assembly
        .GetReferencedAssemblies()
        .Select(r => r.Name!)
        .Where(n => n.StartsWith("Hrms.", StringComparison.Ordinal));

    public static bool IsModuleAssemblyName(string name) => ModuleAssemblyPattern().IsMatch(name);

    /// <summary>Number of module projects on disk (src/Modules/&lt;Module&gt;/&lt;Layer&gt;/*.csproj), to catch undiscovered modules.</summary>
    public static int ModuleProjectCountOnDisk() =>
        Directory.GetFiles(Path.Combine(RepoRoot(), "src", "Modules"), "*.csproj", SearchOption.AllDirectories).Length;

    /// <summary>The folder that holds Hrms.sln, found by walking up from the test output directory.</summary>
    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Hrms.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate Hrms.sln above the test output directory.");
    }
}
