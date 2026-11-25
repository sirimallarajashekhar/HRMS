using Hrms.Core.Domain.LegalEntities;
using Hrms.Core.Domain.Tenants;
using Hrms.Core.Infrastructure;
using Hrms.Core.Infrastructure.Persistence;
using Hrms.Core.IntegrationTests.Persistence;
using Hrms.SharedKernel;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;

namespace Hrms.Core.IntegrationTests.Core;

/// <summary>Creates a fresh database per test class and builds its schema from the real migrations, exactly as a deployment would.</summary>
public static class CoreTestDb
{
    public static readonly DateTime Created = new(2026, 11, 25, 10, 30, 0, DateTimeKind.Utc);

    /// <summary>Returns a connection string for a new, migrated database. Uses root because the app user cannot create databases.</summary>
    public static async Task<string> CreateMigratedAsync(MySqlFixture mysql)
    {
        var database = $"core_{Guid.NewGuid():N}";
        var admin = new MySqlConnectionStringBuilder(mysql.ConnectionString) { UserID = "root", Database = string.Empty };

        await using (var connection = new MySqlConnection(admin.ConnectionString))
        {
            await connection.OpenAsync();
            await using var create = new MySqlCommand($"CREATE DATABASE `{database}` CHARACTER SET utf8mb4", connection);
            await create.ExecuteNonQueryAsync();
        }

        admin.Database = database;
        var connectionString = admin.ConnectionString;
        await using var context = Context(connectionString, tenant: null);
        await context.Database.MigrateAsync();
        return connectionString;
    }

    public static CoreDbContext Context(string connectionString, TenantId? tenant, TimeProvider? time = null, Guid? actor = null)
    {
        var tenantContext = new TenantContext();
        if (tenant is { } id)
        {
            tenantContext.Set(id);
        }

        var options = (DbContextOptions<CoreDbContext>)CoreInfrastructureExtensions
            .UseCoreMySql(new DbContextOptionsBuilder<CoreDbContext>(), connectionString)
            .Options;

        return new CoreDbContext(options, tenantContext, new FixedActor(actor), time ?? new FixedTimeProvider(new DateTimeOffset(Created)));
    }

    public static async Task<Tenant> SeedTenantAsync(string connectionString, string? code = null, bool suspended = false)
    {
        var tenant = Tenant.Create(code ?? $"T{Guid.NewGuid():N}"[..12], "Test tenant", Created).Value;
        if (suspended)
        {
            tenant.Suspend();
        }

        await using var context = Context(connectionString, tenant: null);
        context.Tenants.Add(tenant);
        await context.SaveChangesAsync();
        return tenant;
    }

    public static LegalEntity NewLegalEntity(string? code = null) =>
        LegalEntity.Create(
            code ?? $"LE{Guid.NewGuid():N}"[..12],
            "Acme India Pvt Ltd",
            "IN",
            "INR",
            4,
            "IN",
            "en-IN",
            "Asia/Kolkata").Value;

    public static async Task<LegalEntity> SeedLegalEntityAsync(string connectionString, TenantId tenant, string? code = null)
    {
        var entity = NewLegalEntity(code);
        await using var context = Context(connectionString, tenant);
        context.LegalEntities.Add(entity);
        await context.SaveChangesAsync();
        return entity;
    }
}
