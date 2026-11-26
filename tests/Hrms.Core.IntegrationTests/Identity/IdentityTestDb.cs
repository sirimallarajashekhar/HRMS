using Hrms.Core.IntegrationTests.Persistence;
using Hrms.Identity.Application;
using Hrms.Identity.Domain.Roles;
using Hrms.Identity.Domain.Users;
using Hrms.Identity.Infrastructure;
using Hrms.Identity.Infrastructure.Persistence;
using Hrms.Identity.Infrastructure.Security;
using Hrms.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace Hrms.Core.IntegrationTests.Identity;

/// <summary>A clock tests can move, so cache expiry and lockout windows are exercised without sleeping.</summary>
public sealed class MovableClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>Builds a fresh migrated Identity database per test class, exactly as a deployment would.</summary>
public static class IdentityTestDb
{
    public static readonly DateTime Start = new(2026, 11, 26, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>The cheapest hashing the options validator allows, so tests stay fast without weakening what production runs.</summary>
    public static IPasswordHasher FastHasher() =>
        new IdentityPasswordHasher(Options.Create(new PasswordHashingOptions { IterationCount = PasswordHashingOptions.MinimumIterationCount }));

    public static async Task<string> CreateMigratedAsync(MySqlFixture mysql)
    {
        var database = $"identity_{Guid.NewGuid():N}";
        var admin = new MySqlConnectionStringBuilder(mysql.ConnectionString) { UserID = "root", Database = string.Empty };

        await using (var connection = new MySqlConnection(admin.ConnectionString))
        {
            await connection.OpenAsync();
            await using var create = new MySqlCommand($"CREATE DATABASE `{database}` CHARACTER SET utf8mb4", connection);
            await create.ExecuteNonQueryAsync();
        }

        admin.Database = database;
        await using var context = Context(admin.ConnectionString, tenant: null);
        await context.Database.MigrateAsync();
        return admin.ConnectionString;
    }

    public static IdentityDbContext Context(string connectionString, TenantId? tenant, TimeProvider? time = null, Guid? actor = null)
    {
        var tenantContext = new TenantContext();
        if (tenant is { } id)
        {
            tenantContext.Set(id);
        }

        var options = (DbContextOptions<IdentityDbContext>)IdentityInfrastructureExtensions
            .UseIdentityMySql(new DbContextOptionsBuilder<IdentityDbContext>(), connectionString)
            .Options;

        return new IdentityDbContext(options, tenantContext, new FixedActor(actor), time ?? new MovableClock(new DateTimeOffset(Start)));
    }

    public static TenantIdentitySeeder Seeder(IdentityDbContext context, TenantId tenant, TimeProvider? time = null)
    {
        var tenantContext = new TenantContext();
        tenantContext.Set(tenant);
        return new TenantIdentitySeeder(context, FastHasher(), tenantContext, time ?? new MovableClock(new DateTimeOffset(Start)));
    }

    /// <summary>Seeds the catalogue (once, globally) and the default roles for a tenant.</summary>
    public static async Task SeedTenantAsync(string connectionString, TenantId tenant)
    {
        await using var context = Context(connectionString, tenant);
        var seeder = Seeder(context, tenant);
        await seeder.SyncPermissionCatalogueAsync(CancellationToken.None);
        await seeder.SeedDefaultRolesAsync(CancellationToken.None);
    }

    public static async Task<UserAccount> AddUserAsync(string connectionString, TenantId tenant, string email)
    {
        var user = UserAccount.Create(email, "Test User", FastHasher().Hash("a-very-long-password"), Start).Value;
        await using var context = Context(connectionString, tenant);
        context.Users.Add(user);
        await context.SaveChangesAsync();
        return user;
    }

    public static async Task<Role> RoleAsync(string connectionString, TenantId tenant, string code)
    {
        await using var context = Context(connectionString, tenant);
        return await context.Roles.AsNoTracking().SingleAsync(r => r.Code == code);
    }
}
