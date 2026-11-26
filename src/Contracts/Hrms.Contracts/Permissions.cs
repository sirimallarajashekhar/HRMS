using System.Reflection;

namespace Hrms.Contracts;

/// <summary>
/// The permission catalogue. A permission code is <c>module.entity.action</c> in lower snake case. Each module adds a nested
/// class with its codes as constants when it ships an endpoint; nothing else may invent a code, because
/// <c>[RequirePermission]</c> rejects codes that are not listed here (a typo must not silently deny or, worse, allow).
/// </summary>
public static class Permissions
{
    public static class Identity
    {
        public const string UserView = "identity.user.view";
        public const string UserManage = "identity.user.manage";
        public const string RoleView = "identity.role.view";
        public const string RoleManage = "identity.role.manage";
        public const string SecurityPolicyManage = "identity.security_policy.manage";

        /// <summary>See sensitive fields (bank account, national id, salary) unmasked.</summary>
        public const string SensitiveView = "identity.sensitive.view";
    }

    public static class Core
    {
        public const string LegalEntityView = "core.legal_entity.view";
        public const string LegalEntityManage = "core.legal_entity.manage";
    }

    /// <summary>Every code in the catalogue, discovered from the nested classes' constants.</summary>
    public static IReadOnlySet<string> All { get; } = typeof(Permissions)
        .GetNestedTypes(BindingFlags.Public | BindingFlags.Static)
        .SelectMany(module => module.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy))
        .Where(field => field is { IsLiteral: true, IsInitOnly: false } && field.FieldType == typeof(string))
        .Select(field => (string)field.GetRawConstantValue()!)
        .ToHashSet(StringComparer.Ordinal);

    public static bool IsKnown(string? code) => code is not null && All.Contains(code);
}
