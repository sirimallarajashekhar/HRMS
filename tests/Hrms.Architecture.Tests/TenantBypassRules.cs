using System.Text.RegularExpressions;

namespace Hrms.Architecture.Tests;

/// <summary>
/// Source-level rules for the ways code can step around the tenant filter. Type-dependency tools cannot see method calls, so
/// these scan the product source. Each forbidden API has a short allowlist of the files that legitimately use it.
/// </summary>
public static partial class TenantBypassRules
{
    public sealed record Rule(string Name, Regex Pattern, string[] AllowedPathFragments, string Why);

    public static readonly Rule[] Rules =
    [
        new(
            "IgnoreQueryFilters",
            new Regex(@"\bIgnoreQueryFilters\s*\(", RegexOptions.CultureInvariant),
            [],
            "turns off the tenant and soft-delete filters; if a platform operation truly needs it, add a named scope and allow that file here"),
        new(
            "EF raw SQL (FromSql*, ExecuteSql*, SqlQuery*)",
            new Regex(@"\b(FromSql\w*|ExecuteSql\w*|SqlQuery\w*)\s*[<(]", RegexOptions.CultureInvariant),
            [],
            "raw SQL through EF skips the query filter; use a LINQ query or TenantDbConnection"),
        new(
            "EF bulk writes (ExecuteUpdate*, ExecuteDelete*)",
            new Regex(@"\b(ExecuteUpdate|ExecuteDelete)(Async)?\s*[<(]", RegexOptions.CultureInvariant),
            [],
            "bulk writes skip SaveChanges, so nothing is stamped or audited; load the entities and save them"),
        new(
            "Direct MySQL or Dapper access",
            new Regex(@"\b(new\s+MySqlConnection\s*\(|MySqlDataSource\b|using\s+Dapper\s*;|DbDataSource\b)", RegexOptions.CultureInvariant),
            [
                "src/Platform/Hrms.Persistence/", // the tenant-bound connection and the shared data source live here
                "src/Host/Hrms.Api/Health/DependencyHealthChecks.cs", // connectivity probe only, runs no tenant data
            ],
            "hand-written SQL must go through TenantDbConnection, which binds and checks @TenantId"),
    ];

    /// <summary>Returns "file: rule" for every product source file that uses a forbidden API outside its allowlist.</summary>
    public static IReadOnlyList<string> FindViolations(string repoRoot, IEnumerable<Rule>? rules = null)
    {
        var violations = new List<string>();
        var sourceRoot = Path.Combine(repoRoot, "src");

        foreach (var path in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(repoRoot, path).Replace('\\', '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal)
                || relative.Contains("/Migrations/", StringComparison.Ordinal))
            {
                continue;
            }

            violations.AddRange(FindViolationsInSource(relative, File.ReadAllText(path), rules ?? Rules));
        }

        return violations;
    }

    public static IReadOnlyList<string> FindViolationsInSource(string relativePath, string source, IEnumerable<Rule> rules)
    {
        var code = StripComments(source);
        return rules
            .Where(rule => !rule.AllowedPathFragments.Any(allowed => relativePath.StartsWith(allowed, StringComparison.Ordinal)))
            .Where(rule => rule.Pattern.IsMatch(code))
            .Select(rule => $"{relativePath}: {rule.Name} - {rule.Why}")
            .ToList();
    }

    private static string StripComments(string source) => LineComment().Replace(BlockComment().Replace(source, " "), string.Empty);

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex BlockComment();

    [GeneratedRegex(@"//[^\r\n]*", RegexOptions.CultureInvariant)]
    private static partial Regex LineComment();
}
