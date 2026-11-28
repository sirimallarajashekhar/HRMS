using System.Diagnostics;
using FluentAssertions;
using Hrms.Persistence;
using Hrms.Persistence.Auditing;
using Hrms.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace Hrms.Core.IntegrationTests.Persistence;

/// <summary>The shape the two load records share, so that auditing is the only difference between them.</summary>
public abstract class LoadRecord : TenantEntity
{
    public string Code { get; set; } = string.Empty;

    public string FirstName { get; set; } = string.Empty;

    public string LastName { get; set; } = string.Empty;

    public DateOnly JoiningDate { get; set; }

    public int NoticeDays { get; set; }

    public Guid UnitId { get; set; }
}

public sealed class PlainLoadRecord : LoadRecord;

[Audited]
public sealed class AuditedLoadRecord : LoadRecord;

public sealed class LoadContext(DbContextOptions<LoadContext> options, ITenantContext tenant, IActorContext actor, TimeProvider time)
    : TenantDbContext(options, tenant, actor, time)
{
    protected override bool OwnsAuditTable => true;

    public DbSet<PlainLoadRecord> Plain => Set<PlainLoadRecord>();

    public DbSet<AuditedLoadRecord> Audited => Set<AuditedLoadRecord>();

    public DbSet<AuditEntry> Audit => Set<AuditEntry>();

    protected override void ConfigureEntities(ModelBuilder modelBuilder)
    {
        // The base class is not mapped: each record has its own table.
        modelBuilder.Entity<PlainLoadRecord>().ToTable("load_plain").HasBaseType((Type?)null);
        modelBuilder.Entity<AuditedLoadRecord>().ToTable("load_audited").HasBaseType((Type?)null);
    }
}

/// <summary>
/// What auditing adds to a bulk load. 60,000 records is what loading 20,000 employees writes (employee, job and personal data). The same
/// load runs with and without auditing, in batches of 500. The numbers are in the test output; the assertion only catches a change in
/// order of magnitude, as no target has been set for imports. The audit table here has no partitions or secondary indexes (the model
/// does not carry the migration's), so a real load costs somewhat more.
/// </summary>
public sealed class AuditCaptureCostTests(MySqlFixture mysql, ITestOutputHelper output) : IClassFixture<MySqlFixture>
{
    private const int Records = 60_000;
    private const int BatchSize = 500;
    private const double GenerousRatio = 5;

    private readonly TenantId _tenant = PersistenceTestDb.NewTenant();

    private LoadContext Context()
    {
        var tenantContext = new TenantContext();
        tenantContext.Set(_tenant);
        var options = new DbContextOptionsBuilder<LoadContext>()
            .UseMySql(PersistenceTestDb.ConnectionString(mysql), new MySqlServerVersion(new Version(8, 4, 0)))
            .Options;
        return new LoadContext(options, tenantContext, new FixedActor(Guid.CreateVersion7()), TimeProvider.System);
    }

    private async Task<TimeSpan> LoadAsync<T>(int count)
        where T : LoadRecord, new()
    {
        var unit = Guid.CreateVersion7();
        var watch = Stopwatch.StartNew();
        for (var start = 0; start < count; start += BatchSize)
        {
            await using var context = Context();
            context.AddRange(Enumerable.Range(start, Math.Min(BatchSize, count - start)).Select(n => new T
            {
                Code = $"L{n:D6}",
                FirstName = $"First{n}",
                LastName = $"Last{n:D6}",
                JoiningDate = new DateOnly(2020, 1, 1),
                NoticeDays = 30,
                UnitId = unit,
            }));
            await context.SaveChangesAsync();
        }

        return watch.Elapsed;
    }

    [Fact]
    public async Task BulkLoad_WithAuditing_StaysWithinAnOrderOfMagnitudeOfTheSameLoadWithout()
    {
        await using (var context = Context())
        {
            await context.Database.EnsureCreatedAsync();
        }

        // One batch of each first, so neither measurement pays for model building and the first connection.
        await LoadAsync<PlainLoadRecord>(BatchSize);
        await LoadAsync<AuditedLoadRecord>(BatchSize);

        var plain = await LoadAsync<PlainLoadRecord>(Records);
        var audited = await LoadAsync<AuditedLoadRecord>(Records);

        var ratio = audited / plain;
        output.WriteLine($"{Records} records: {plain.TotalSeconds:F1} s without auditing, {audited.TotalSeconds:F1} s with it ({ratio:F2} times)");

        await using var check = Context();
        (await check.Audit.CountAsync()).Should().Be(Records + BatchSize);
        ratio.Should().BeLessThan(GenerousRatio);
    }
}
