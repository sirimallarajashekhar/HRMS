using Hrms.SharedKernel;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Hrms.Architecture.Tests;

/// <summary>Every tenant-owned table is filtered by tenant and indexed with tenant_id first (CLAUDE.md: indexes lead with tenant_id).</summary>
public static class TenantModelRule
{
    public static IReadOnlyList<string> FindViolations(IModel model)
    {
        var violations = new List<string>();

        foreach (var entityType in model.GetEntityTypes().Where(e => e.BaseType is null && e.ClrType.IsAssignableTo(typeof(TenantEntity))))
        {
            if (entityType.GetQueryFilter() is null)
            {
                violations.Add($"{entityType.DisplayName()}: no query filter, so rows of every tenant are visible");
            }

            var hasTenantLeadingIndex = entityType.GetIndexes().Any(index => index.Properties[0].Name == nameof(TenantEntity.TenantId))
                                        || entityType.FindPrimaryKey()?.Properties[0].Name == nameof(TenantEntity.TenantId);
            if (!hasTenantLeadingIndex)
            {
                violations.Add($"{entityType.DisplayName()}: no index leads with tenant_id, so tenant-scoped queries scan the whole table");
            }
        }

        return violations;
    }
}
