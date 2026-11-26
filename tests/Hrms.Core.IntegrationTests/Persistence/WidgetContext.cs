using Hrms.Persistence;
using Hrms.SharedKernel;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Testcontainers.MySql;

namespace Hrms.Core.IntegrationTests.Persistence;

/// <summary>A minimal tenant-owned entity used to exercise TenantDbContext against real MySQL.</summary>
public sealed class Widget : TenantEntity
{
    public Widget(string code, string name)
    {
        Code = code;
        Name = name;
    }

    private Widget()
    {
        Code = string.Empty;
        Name = string.Empty;
    }

    public string Code { get; set; }

    public string Name { get; set; }

    public DateTime? ExpiresAt { get; set; }
}

public sealed class WidgetContext(
    DbContextOptions<WidgetContext> options,
    ITenantContext tenant,
    IActorContext actor,
    TimeProvider time) : TenantDbContext(options, tenant, actor, time)
{
    public DbSet<Widget> Widgets => Set<Widget>();

    protected override void ConfigureEntities(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<Widget>().Property(w => w.Code).HasMaxLength(50);
}

/// <summary>Entity with a decimal and no declared precision: the model must refuse to build.</summary>
public sealed class Priced : TenantEntity
{
    public decimal Price { get; set; }
}

public sealed class PricedContext(
    DbContextOptions<PricedContext> options,
    ITenantContext tenant,
    IActorContext actor,
    TimeProvider time) : TenantDbContext(options, tenant, actor, time)
{
    public DbSet<Priced> Priced => Set<Priced>();
}

public sealed class FixedActor(Guid? actorId) : IActorContext
{
    public Guid? ActorId => actorId;
}

public sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

public static class PersistenceTestDb
{
    private static readonly SemaphoreSlim SchemaLock = new(1, 1);
    private static readonly HashSet<string> CreatedFor = [];

    /// <summary>Binary16 stores Guids as 16 raw bytes in RFC order, which keeps UUIDv7 ids index-friendly.</summary>
    public static string ConnectionString(MySqlFixture mysql) => new MySqlConnectionStringBuilder(mysql.ConnectionString)
    {
        GuidFormat = MySqlGuidFormat.Binary16,
    }.ConnectionString;

    public static WidgetContext Widgets(
        MySqlFixture mysql,
        TenantId? tenant,
        TimeProvider? time = null,
        Guid? actor = null)
    {
        var tenantContext = new TenantContext();
        if (tenant is { } id)
        {
            tenantContext.Set(id);
        }

        var options = new DbContextOptionsBuilder<WidgetContext>()
            .UseMySql(ConnectionString(mysql), new MySqlServerVersion(new Version(8, 4, 0)))
            .Options;

        return new WidgetContext(options, tenantContext, new FixedActor(actor), time ?? TimeProvider.System);
    }

    public static PricedContext Priced(MySqlFixture mysql)
    {
        var options = new DbContextOptionsBuilder<PricedContext>()
            .UseMySql(ConnectionString(mysql), new MySqlServerVersion(new Version(8, 4, 0)))
            .Options;

        return new PricedContext(options, new TenantContext(), new FixedActor(null), TimeProvider.System);
    }

    public static TenantId NewTenant() => new(Guid.CreateVersion7());

    public static async Task EnsureSchemaAsync(MySqlFixture mysql)
    {
        await SchemaLock.WaitAsync();
        try
        {
            if (CreatedFor.Add(mysql.ConnectionString))
            {
                await using var context = Widgets(mysql, tenant: null);
                await context.Database.EnsureCreatedAsync();
            }
        }
        finally
        {
            SchemaLock.Release();
        }
    }
}
