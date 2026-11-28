using System.Text.Json;
using FluentAssertions;
using Hrms.Persistence;
using Hrms.Persistence.Auditing;
using Hrms.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Hrms.Core.IntegrationTests.Persistence;

[Audited]
public sealed class Gadget : TenantEntity
{
    public string Code { get; set; } = string.Empty;

    public string? Note { get; set; }

    [AuditSensitive]
    public string? Secret { get; set; }

    [AuditIgnore]
    public int Hits { get; set; }
}

[Audited(RootType = "gadget", RootKey = nameof(GadgetId))]
public sealed class GadgetPart : TenantEntity
{
    public Guid GadgetId { get; set; }

    public string Name { get; set; } = string.Empty;
}

public sealed class GadgetContext(DbContextOptions<GadgetContext> options, ITenantContext tenant, IActorContext actor, TimeProvider time)
    : TenantDbContext(options, tenant, actor, time)
{
    protected override bool OwnsAuditTable => true;

    public DbSet<Gadget> Gadgets => Set<Gadget>();

    public DbSet<GadgetPart> Parts => Set<GadgetPart>();

    public DbSet<AuditEntry> Audit => Set<AuditEntry>();

    protected override void ConfigureEntities(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Gadget>().ToTable("gadget");
        modelBuilder.Entity<Gadget>().Property(g => g.Code).HasMaxLength(50);
        modelBuilder.Entity<Gadget>().HasIndex(nameof(TenantEntity.TenantId), nameof(Gadget.Code)).IsUnique();
        modelBuilder.Entity<GadgetPart>().ToTable("gadget_part");
    }
}

public sealed class OriginActor(Guid? actorId, AuditOrigin origin) : IActorContext
{
    public Guid? ActorId => actorId;

    public AuditOrigin Origin => origin;
}

public sealed class AuditCaptureTests(MySqlFixture mysql) : IClassFixture<MySqlFixture>, IAsyncLifetime
{
    private static readonly Guid UserId = Guid.CreateVersion7();
    private static readonly AuditOrigin Origin = new(AuditActorType.User, null, "10.0.0.7", "Correction", "trace-1");

    private readonly TenantId _tenant = PersistenceTestDb.NewTenant();

    public async Task InitializeAsync()
    {
        // This class has its own container, so the database starts empty and gets these tables.
        await using var context = Context(_tenant);
        await context.Database.EnsureCreatedAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private GadgetContext Context(TenantId tenant)
    {
        var tenantContext = new TenantContext();
        tenantContext.Set(tenant);
        var options = new DbContextOptionsBuilder<GadgetContext>()
            .UseMySql(PersistenceTestDb.ConnectionString(mysql), new MySqlServerVersion(new Version(8, 4, 0)))
            .Options;
        return new GadgetContext(options, tenantContext, new OriginActor(UserId, Origin), TimeProvider.System);
    }

    private async Task<List<AuditEntry>> HistoryAsync(Guid rootId)
    {
        await using var context = Context(_tenant);
        return await context.Audit.AsNoTracking().Where(a => a.RootId == rootId).OrderBy(a => a.OccurredAt).ThenBy(a => a.Id).ToListAsync();
    }

    private static Dictionary<string, JsonElement> Fields(AuditEntry entry) =>
        JsonDocument.Parse(entry.Changes).RootElement.EnumerateArray().ToDictionary(c => c.GetProperty("field").GetString()!);

    private async Task<Gadget> AddGadgetAsync(string code, string? secret = null)
    {
        await using var context = Context(_tenant);
        var gadget = new Gadget { Code = code, Secret = secret };
        context.Gadgets.Add(gadget);
        await context.SaveChangesAsync();
        return gadget;
    }

    [Fact]
    public async Task SaveChanges_WhenARecordIsCreated_RecordsValuesAndTheCaller()
    {
        var gadget = await AddGadgetAsync("CREATE");

        var entry = (await HistoryAsync(gadget.Id)).Should().ContainSingle().Subject;
        entry.Action.Should().Be(AuditAction.Created);
        entry.EntityType.Should().Be("gadget");
        entry.RecordId.Should().Be(gadget.Id);
        entry.ActorId.Should().Be(UserId);
        entry.ActorType.Should().Be(AuditActorType.User);
        entry.IpAddress.Should().Be("10.0.0.7");
        entry.Reason.Should().Be("Correction");
        entry.CorrelationId.Should().Be("trace-1");
        Fields(entry).Keys.Should().Equal("code");
        Fields(entry)["code"].GetProperty("new").GetString().Should().Be("CREATE");
    }

    [Fact]
    public async Task SaveChanges_WhenFieldsChange_RecordsOnlyThoseWithOldAndNewValues()
    {
        var gadget = await AddGadgetAsync("UPDATE");
        await using (var context = Context(_tenant))
        {
            var loaded = await context.Gadgets.SingleAsync(g => g.Id == gadget.Id);
            loaded.Note = "changed";
            var written = await context.SaveChangesAsync();

            written.Should().Be(1, "audit rows are not part of the caller's count");
        }

        var update = (await HistoryAsync(gadget.Id))[1];
        update.Action.Should().Be(AuditAction.Updated);
        Fields(update).Keys.Should().Equal("note");
        Fields(update)["note"].GetProperty("old").ValueKind.Should().Be(JsonValueKind.Null);
        Fields(update)["note"].GetProperty("new").GetString().Should().Be("changed");
    }

    [Fact]
    public async Task SaveChanges_WhenASensitiveFieldChanges_StoresNoValue()
    {
        var gadget = await AddGadgetAsync("SECRET", secret: "first-secret-value");
        await using (var context = Context(_tenant))
        {
            (await context.Gadgets.SingleAsync(g => g.Id == gadget.Id)).Secret = "second-secret-value";
            await context.SaveChangesAsync();
        }

        var history = await HistoryAsync(gadget.Id);

        history.Should().HaveCount(2);
        history.Should().AllSatisfy(e => e.Changes.Should().NotContain("secret-value"));
        Fields(history[1])["secret"].GetProperty("sensitive").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task SaveChanges_WhenOnlyAnIgnoredFieldChanges_WritesNothing()
    {
        var gadget = await AddGadgetAsync("IGNORED");
        await using (var context = Context(_tenant))
        {
            (await context.Gadgets.SingleAsync(g => g.Id == gadget.Id)).Hits = 5;
            await context.SaveChangesAsync();
        }

        (await HistoryAsync(gadget.Id)).Should().ContainSingle();
    }

    [Fact]
    public async Task SaveChanges_WhenARecordIsDeleted_RecordsADeletion()
    {
        var gadget = await AddGadgetAsync("DELETE");
        await using (var context = Context(_tenant))
        {
            context.Gadgets.Remove(await context.Gadgets.SingleAsync(g => g.Id == gadget.Id));
            await context.SaveChangesAsync();
        }

        (await HistoryAsync(gadget.Id)).Select(e => e.Action).Should().Equal(AuditAction.Created, AuditAction.Deleted);
    }

    /// <summary>For a record created before the log existed, the deletion is the only entry that can say what it was.</summary>
    [Fact]
    public async Task SaveChanges_WhenARecordIsDeleted_RecordsWhatItHeld_WithoutSensitiveOrIgnoredValues()
    {
        var gadget = await AddGadgetAsync("HELD", secret: "held-secret-value");
        await using (var context = Context(_tenant))
        {
            context.Gadgets.Remove(await context.Gadgets.SingleAsync(g => g.Id == gadget.Id));
            await context.SaveChangesAsync();
        }

        var fields = Fields((await HistoryAsync(gadget.Id))[1]);

        fields.Keys.Should().BeEquivalentTo("code", "secret");
        fields["code"].GetProperty("old").GetString().Should().Be("HELD");
        fields["code"].GetProperty("new").ValueKind.Should().Be(JsonValueKind.Null);
        fields["secret"].GetProperty("sensitive").GetBoolean().Should().BeTrue();
        fields["secret"].GetRawText().Should().NotContain("held-secret-value");
    }

    [Fact]
    public async Task SaveChanges_WhenARecordIsEditedAndDeletedTogether_RecordsTheStoredAndTheNewValue()
    {
        var gadget = await AddGadgetAsync("EDITDEL");
        await using (var context = Context(_tenant))
        {
            var loaded = await context.Gadgets.SingleAsync(g => g.Id == gadget.Id);
            loaded.Code = "EDITDEL-2";
            loaded.Note = "added";
            context.Gadgets.Remove(loaded);
            await context.SaveChangesAsync();
        }

        var deletion = (await HistoryAsync(gadget.Id))[1];

        deletion.Action.Should().Be(AuditAction.Deleted);
        var fields = Fields(deletion);
        fields["code"].GetProperty("old").GetString().Should().Be("EDITDEL");
        fields["code"].GetProperty("new").GetString().Should().Be("EDITDEL-2");
        fields["note"].GetProperty("old").ValueKind.Should().Be(JsonValueKind.Null);
        fields["note"].GetProperty("new").GetString().Should().Be("added");
    }

    [Fact]
    public async Task SaveChanges_WhenARecordHasAnOwner_FilesItUnderTheOwner()
    {
        var gadget = await AddGadgetAsync("OWNER");
        var part = new GadgetPart { GadgetId = gadget.Id, Name = "lens" };
        await using (var context = Context(_tenant))
        {
            context.Parts.Add(part);
            await context.SaveChangesAsync();
        }

        var entry = (await HistoryAsync(gadget.Id))[1];
        entry.EntityType.Should().Be("gadget_part");
        entry.RecordId.Should().Be(part.Id);
        entry.RootType.Should().Be("gadget");
    }

    [Fact]
    public async Task SaveChanges_WhenAFailedSaveIsRetried_RecordsTheChangeOnce()
    {
        await AddGadgetAsync("TAKEN");
        await using var context = Context(_tenant);
        var gadget = new Gadget { Code = "TAKEN" };
        context.Gadgets.Add(gadget);

        await context.Invoking(c => c.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>();
        context.ChangeTracker.Entries<AuditEntry>().Should().BeEmpty();

        gadget.Code = "FREE";
        await context.SaveChangesAsync();

        var entry = (await HistoryAsync(gadget.Id)).Should().ContainSingle().Subject;
        Fields(entry)["code"].GetProperty("new").GetString().Should().Be("FREE");
    }

    [Fact]
    public async Task SaveChanges_WhenTheTransactionRollsBack_LeavesNoAuditRow()
    {
        var gadget = new Gadget { Code = "ROLLBACK" };
        await using (var context = Context(_tenant))
        {
            await using var transaction = await context.Database.BeginTransactionAsync();
            context.Gadgets.Add(gadget);
            await context.SaveChangesAsync();
            await transaction.RollbackAsync();
        }

        (await HistoryAsync(gadget.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task AuditLog_WhenReadByAnotherTenant_ReturnsNothing()
    {
        var gadget = await AddGadgetAsync("ISOLATED");
        await using var other = Context(PersistenceTestDb.NewTenant());

        (await other.Audit.AsNoTracking().Where(a => a.RootId == gadget.Id).ToListAsync()).Should().BeEmpty();
    }

    [Theory]
    [InlineData(EntityState.Modified)]
    [InlineData(EntityState.Deleted)]
    public async Task SaveChanges_WhenSomeoneEditsTheAuditLog_Refuses(EntityState state)
    {
        var gadget = await AddGadgetAsync("APPEND-" + state);
        await using var context = Context(_tenant);
        var entry = await context.Audit.SingleAsync(a => a.RootId == gadget.Id);
        context.Entry(entry).State = state;

        await context.Invoking(c => c.SaveChangesAsync()).Should().ThrowAsync<InvalidOperationException>().WithMessage("*append-only*");
    }
}
