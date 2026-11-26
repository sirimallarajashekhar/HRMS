using System.Linq.Expressions;
using Hrms.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Hrms.Persistence;

public static class TenantUniqueIndexExtensions
{
    /// <summary>
    /// Makes a string property unique per tenant among live rows. A soft-deleted row keeps its value but stops counting, so
    /// the code can be reused. MySQL has no filtered unique indexes, so this adds a generated column that is NULL for deleted
    /// rows (NULLs never collide) and a unique index on (tenant_id, that column). tenant_id leads the index, so it also serves
    /// every tenant-scoped scan by that prefix.
    /// </summary>
    public static IndexBuilder<T> HasUniquePerTenant<T>(
        this EntityTypeBuilder<T> builder,
        Expression<Func<T, string>> property,
        int maxLength)
        where T : TenantEntity
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(property);

        var propertyName = (property.Body as MemberExpression)?.Member.Name
            ?? throw new ArgumentException("Select a single property, for example e => e.Code.", nameof(property));
        var columnName = SnakeCase.From(propertyName);
        var activeName = $"Active{propertyName}";

        builder.Property<string?>(activeName)
            .HasMaxLength(maxLength)
            .HasComputedColumnSql($"IF(`is_deleted` = 0, `{columnName}`, NULL)", stored: false);

        var table = SnakeCase.From(typeof(T).Name);
        return builder
            .HasIndex(nameof(TenantEntity.TenantId), activeName)
            .IsUnique()
            .HasDatabaseName($"ux_{table}_tenant_{columnName}");
    }
}
