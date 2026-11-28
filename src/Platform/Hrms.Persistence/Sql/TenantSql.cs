using System.Text;
using System.Text.RegularExpressions;

namespace Hrms.Persistence.Sql;

/// <summary>
/// A lint for hand-written SQL run through <see cref="TenantDbConnection"/>. It makes forgetting the tenant filter impossible
/// to do by accident: every statement must bind the tenant to <c>@TenantId</c>, comparing it to a <c>tenant_id</c> column
/// (or inserting it). It deliberately accepts only SELECT, WITH, INSERT, UPDATE and DELETE, one statement at a time.
/// <para>
/// This is a lint, not a proof. It cannot know whether every table in a join is filtered, whether an OR defeats the predicate,
/// or whether soft-deleted rows (<c>is_deleted</c>) are excluded: each Dapper query still needs its own tenant-isolation test.
/// </para>
/// </summary>
public static partial class TenantSql
{
    /// <summary>The parameter name the connection binds to the current tenant. Callers may not supply it.</summary>
    public const string TenantParameter = "TenantId";

    private static readonly string[] AllowedVerbs = ["select", "with", "insert", "update", "delete"];

    /// <summary>Throws <see cref="ArgumentException"/> when the statement could run without the tenant filter.</summary>
    public static void Validate(string sql)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        var code = StripCommentsAndLiterals(sql).Trim();
        if (code.EndsWith(';'))
        {
            code = code[..^1].TrimEnd();
        }

        if (code.Contains(';', StringComparison.Ordinal))
        {
            throw Reject("Only one statement may be run at a time.");
        }

        var verb = FirstWord().Match(code).Value.ToLowerInvariant();
        if (!AllowedVerbs.Contains(verb, StringComparer.Ordinal))
        {
            throw Reject($"Only SELECT, WITH, INSERT, UPDATE and DELETE are allowed (found '{verb}').");
        }

        RejectAuditLogWrites(code, verb);

        if (verb == "insert")
        {
            ValidateInsert(code);
            return;
        }

        if (!TenantPredicate().IsMatch(code) && !ReversedTenantPredicate().IsMatch(code))
        {
            throw Reject("The statement must filter on the tenant: add 'tenant_id = @TenantId' to its WHERE or JOIN condition.");
        }

        if (verb == "update" && SetClause().Match(code) is { Success: true } set && TenantColumn().IsMatch(set.Groups["set"].Value))
        {
            throw Reject("tenant_id is immutable and cannot be assigned in an UPDATE.");
        }
    }

    /// <summary>
    /// Only <see cref="TenantDbContext"/> writes the audit log. A writing statement may not name the table at all, even only to read
    /// it: telling the target from a joined table would need a SQL parser. WITH can lead into an UPDATE or DELETE.
    /// </summary>
    private static void RejectAuditLogWrites(string code, string verb)
    {
        var writes = verb is "insert" or "update" or "delete" || (verb == "with" && WritingKeyword().IsMatch(code));
        if (writes && AuditLogTable().IsMatch(code))
        {
            throw Reject("The audit log is append-only: a statement that writes may not name audit_log.");
        }
    }

    private static void ValidateInsert(string code)
    {
        if (!TenantColumn().IsMatch(code) || !TenantParameterToken().IsMatch(code))
        {
            throw Reject("An INSERT must set tenant_id from @TenantId.");
        }

        var duplicate = OnDuplicateKeyUpdate().Match(code);
        if (duplicate.Success && TenantColumn().IsMatch(duplicate.Groups["update"].Value))
        {
            throw Reject("tenant_id is immutable and cannot be assigned in ON DUPLICATE KEY UPDATE.");
        }
    }

    private static ArgumentException Reject(string reason) => new($"Unsafe tenant SQL: {reason}");

    /// <summary>
    /// Removes comments and replaces string literals with '', so a tenant predicate hidden in either cannot satisfy the lint.
    /// Backticks are dropped so quoted identifiers match. MySQL executable comments (/*! ... */) are rejected outright.
    /// </summary>
    internal static string StripCommentsAndLiterals(string sql)
    {
        var result = new StringBuilder(sql.Length);
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            var next = i + 1 < sql.Length ? sql[i + 1] : '\0';

            if (c == '/' && next == '*')
            {
                if (i + 2 < sql.Length && sql[i + 2] is '!' or '+')
                {
                    throw Reject("Executable and optimizer-hint comments (/*! and /*+) are not allowed.");
                }

                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    throw Reject("Unterminated comment.");
                }

                result.Append(' ');
                i = end + 2;
            }
            else if (c == '#' || (c == '-' && next == '-' && (i + 2 >= sql.Length || char.IsWhiteSpace(sql[i + 2]) || char.IsControl(sql[i + 2]))))
            {
                var end = sql.IndexOf('\n', i);
                result.Append(' ');
                i = end < 0 ? sql.Length : end + 1;
            }
            else if (c is '\'' or '"')
            {
                i = SkipLiteral(sql, i, c);
                result.Append("''");
            }
            else if (c == '`')
            {
                i++;
            }
            else
            {
                result.Append(c);
                i++;
            }
        }

        return result.ToString();
    }

    private static int SkipLiteral(string sql, int start, char quote)
    {
        var i = start + 1;
        while (i < sql.Length)
        {
            if (sql[i] == '\\')
            {
                i += 2;
            }
            else if (sql[i] == quote)
            {
                if (i + 1 < sql.Length && sql[i + 1] == quote)
                {
                    i += 2; // doubled quote is an escaped quote
                }
                else
                {
                    return i + 1;
                }
            }
            else
            {
                i++;
            }
        }

        throw Reject("Unterminated string literal.");
    }

    [GeneratedRegex(@"^\s*\w+", RegexOptions.CultureInvariant)]
    private static partial Regex FirstWord();

    [GeneratedRegex(@"(?<!\w)audit_log(?!\w)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AuditLogTable();

    [GeneratedRegex(@"\b(insert|update|delete)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WritingKeyword();

    [GeneratedRegex(@"(?<!\w)(?:\w+\.)?tenant_id\s*=\s*@TenantId(?!\w)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TenantPredicate();

    [GeneratedRegex(@"(?<![\w@])@TenantId\s*=\s*(?:\w+\.)?tenant_id(?!\w)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReversedTenantPredicate();

    [GeneratedRegex(@"(?<!\w)tenant_id(?!\w)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TenantColumn();

    [GeneratedRegex(@"(?<![\w@])@TenantId(?!\w)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TenantParameterToken();

    [GeneratedRegex(@"\bset\b(?<set>.*?)(?:\bwhere\b|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex SetClause();

    [GeneratedRegex(@"\bon\s+duplicate\s+key\s+update\b(?<update>.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex OnDuplicateKeyUpdate();
}
