using FluentAssertions;
using Hrms.Persistence;
using Hrms.SharedKernel;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;

namespace Hrms.Core.IntegrationTests.Persistence;

[Collection(MySqlTestGroup.Name)]
public class TenantDbContextTests(MySqlFixture mysql) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 10, 30, 0, TimeSpan.Zero);

    public Task InitializeAsync() => PersistenceTestDb.EnsureSchemaAsync(mysql);

    public Task DisposeAsync() => Task.CompletedTask;

    private static string Code() => Guid.NewGuid().ToString("N")[..12];

    private async Task<Guid> SeedAsync(TenantId tenant, string? code = null, string name = "Seed")
    {
        await using var context = PersistenceTestDb.Widgets(mysql, tenant, new FixedTimeProvider(Now));
        var widget = new Widget(code ?? Code(), name);
        context.Widgets.Add(widget);
        await context.SaveChangesAsync();
        return widget.Id;
    }

    [Fact]
    public async Task Mapping_Widget_UsesSnakeCaseNamesAndBinarySixteenIds()
    {
        await using var connection = new MySqlConnection(mysql.ConnectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand("SHOW CREATE TABLE widget", connection);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();

        var ddl = reader.GetString(1);

        ddl.Should().Contain("`tenant_id`").And.Contain("`created_at`").And.Contain("`created_by`")
            .And.Contain("`updated_at`").And.Contain("`is_deleted`").And.Contain("`row_version`").And.Contain("`expires_at`");
        ddl.Should().MatchRegex("`id` binary\\(16\\)").And.MatchRegex("`tenant_id` binary\\(16\\)");
    }

    [Fact]
    public async Task Query_AsTenantA_NeverReturnsTenantBRows()
    {
        var a = PersistenceTestDb.NewTenant();
        var b = PersistenceTestDb.NewTenant();
        var aId = await SeedAsync(a);
        var bId = await SeedAsync(b);

        await using var asA = PersistenceTestDb.Widgets(mysql, a);
        var visible = await asA.Widgets.AsNoTracking().Select(w => w.Id).ToListAsync();

        visible.Should().Contain(aId).And.NotContain(bId);
        (await asA.Widgets.AsNoTracking().AnyAsync(w => w.Id == bId)).Should().BeFalse();
        (await asA.Widgets.FindAsync(bId)).Should().BeNull();
    }

    [Fact]
    public async Task Query_WithoutATenant_ReturnsNothing()
    {
        await SeedAsync(PersistenceTestDb.NewTenant());

        await using var noTenant = PersistenceTestDb.Widgets(mysql, tenant: null);

        (await noTenant.Widgets.AsNoTracking().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Add_StampsTenantAuditColumnsAndFirstRowVersion()
    {
        var tenant = PersistenceTestDb.NewTenant();
        var actor = Guid.CreateVersion7();
        await using var context = PersistenceTestDb.Widgets(mysql, tenant, new FixedTimeProvider(Now), actor);
        var widget = new Widget(Code(), "Gadget");

        context.Widgets.Add(widget);
        await context.SaveChangesAsync();

        await using var verify = PersistenceTestDb.Widgets(mysql, tenant);
        var stored = await verify.Widgets.AsNoTracking().SingleAsync(w => w.Id == widget.Id);
        stored.TenantId.Should().Be(tenant);
        stored.CreatedAt.Should().Be(Now.UtcDateTime);
        stored.CreatedAt.Kind.Should().Be(DateTimeKind.Utc);
        stored.CreatedBy.Should().Be(actor);
        stored.UpdatedAt.Should().BeNull();
        stored.IsDeleted.Should().BeFalse();
        stored.RowVersion.Should().Be(1);
    }

    [Fact]
    public async Task Add_WithoutATenant_ThrowsTenantNotResolved()
    {
        await using var context = PersistenceTestDb.Widgets(mysql, tenant: null);
        context.Widgets.Add(new Widget(Code(), "Orphan"));

        var act = () => context.SaveChangesAsync();

        await act.Should().ThrowAsync<TenantNotResolvedException>();
    }

    [Fact]
    public async Task Add_EntityOwnedByAnotherTenant_IsRefused()
    {
        var a = PersistenceTestDb.NewTenant();
        var b = PersistenceTestDb.NewTenant();
        await using var asA = PersistenceTestDb.Widgets(mysql, a);
        var widget = new Widget(Code(), "Smuggled");
        var entry = asA.Widgets.Add(widget);
        entry.Property(w => w.TenantId).CurrentValue = b;

        var act = () => asA.SaveChangesAsync();

        await act.Should().ThrowAsync<CrossTenantWriteException>();
    }

    [Fact]
    public async Task Update_StampsAuditAndBumpsRowVersion()
    {
        var tenant = PersistenceTestDb.NewTenant();
        var id = await SeedAsync(tenant);
        var later = new FixedTimeProvider(Now.AddHours(2));
        var actor = Guid.CreateVersion7();

        await using (var context = PersistenceTestDb.Widgets(mysql, tenant, later, actor))
        {
            var widget = await context.Widgets.SingleAsync(w => w.Id == id);
            widget.Name = "Renamed";
            await context.SaveChangesAsync();
        }

        await using var verify = PersistenceTestDb.Widgets(mysql, tenant);
        var stored = await verify.Widgets.AsNoTracking().SingleAsync(w => w.Id == id);
        stored.Name.Should().Be("Renamed");
        stored.RowVersion.Should().Be(2);
        stored.UpdatedAt.Should().Be(Now.AddHours(2).UtcDateTime);
        stored.UpdatedBy.Should().Be(actor);
        stored.CreatedAt.Should().Be(Now.UtcDateTime);
        stored.CreatedBy.Should().BeNull();
    }

    [Fact]
    public async Task Update_WhenAnotherWriterWonTheRace_ThrowsConcurrencyException()
    {
        var tenant = PersistenceTestDb.NewTenant();
        var id = await SeedAsync(tenant);
        await using var first = PersistenceTestDb.Widgets(mysql, tenant);
        await using var second = PersistenceTestDb.Widgets(mysql, tenant);
        var a = await first.Widgets.SingleAsync(w => w.Id == id);
        var b = await second.Widgets.SingleAsync(w => w.Id == id);
        a.Name = "first";
        b.Name = "second";
        await first.SaveChangesAsync();

        var act = () => second.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
    }

    [Fact]
    public async Task Update_OfAnotherTenantsRowAttachedToThisContext_IsRefusedAndLeavesTheRowUntouched()
    {
        var a = PersistenceTestDb.NewTenant();
        var b = PersistenceTestDb.NewTenant();
        var id = await SeedAsync(a, name: "original");
        Widget stolen;
        await using (var asA = PersistenceTestDb.Widgets(mysql, a))
        {
            stolen = await asA.Widgets.AsNoTracking().SingleAsync(w => w.Id == id);
        }

        await using (var asB = PersistenceTestDb.Widgets(mysql, b))
        {
            asB.Widgets.Attach(stolen);
            stolen.Name = "hijacked";
            var act = () => asB.SaveChangesAsync();
            await act.Should().ThrowAsync<CrossTenantWriteException>();
        }

        await using var verify = PersistenceTestDb.Widgets(mysql, a);
        (await verify.Widgets.AsNoTracking().SingleAsync(w => w.Id == id)).Name.Should().Be("original");
    }

    [Fact]
    public async Task Remove_SoftDeletesTheRowAndHidesItFromQueries()
    {
        var tenant = PersistenceTestDb.NewTenant();
        var id = await SeedAsync(tenant);

        await using (var context = PersistenceTestDb.Widgets(mysql, tenant, new FixedTimeProvider(Now.AddDays(1))))
        {
            var widget = await context.Widgets.SingleAsync(w => w.Id == id);
            context.Widgets.Remove(widget);
            await context.SaveChangesAsync();
        }

        await using var verify = PersistenceTestDb.Widgets(mysql, tenant);
        (await verify.Widgets.AsNoTracking().AnyAsync(w => w.Id == id)).Should().BeFalse();
        var raw = await verify.Widgets.IgnoreQueryFilters().AsNoTracking().SingleAsync(w => w.Id == id);
        raw.IsDeleted.Should().BeTrue();
        raw.RowVersion.Should().Be(2);
        raw.UpdatedAt.Should().Be(Now.AddDays(1).UtcDateTime);
    }

    [Fact]
    public async Task Remove_OfAnotherTenantsRowAttachedToThisContext_IsRefused()
    {
        var a = PersistenceTestDb.NewTenant();
        var b = PersistenceTestDb.NewTenant();
        var id = await SeedAsync(a);
        Widget stolen;
        await using (var asA = PersistenceTestDb.Widgets(mysql, a))
        {
            stolen = await asA.Widgets.AsNoTracking().SingleAsync(w => w.Id == id);
        }

        await using (var asB = PersistenceTestDb.Widgets(mysql, b))
        {
            asB.Widgets.Attach(stolen);
            asB.Widgets.Remove(stolen);
            var act = () => asB.SaveChangesAsync();
            await act.Should().ThrowAsync<CrossTenantWriteException>();
        }

        await using var verify = PersistenceTestDb.Widgets(mysql, a);
        (await verify.Widgets.AsNoTracking().AnyAsync(w => w.Id == id)).Should().BeTrue();
    }

    [Fact]
    public async Task DateTime_StoredAsUtc_ComesBackAsUtcAndRejectsLocalValues()
    {
        var tenant = PersistenceTestDb.NewTenant();
        var expires = new DateTime(2027, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        await using (var context = PersistenceTestDb.Widgets(mysql, tenant))
        {
            context.Widgets.Add(new Widget(Code(), "Dated") { ExpiresAt = expires });
            await context.SaveChangesAsync();
        }

        await using var verify = PersistenceTestDb.Widgets(mysql, tenant);
        var stored = await verify.Widgets.AsNoTracking().SingleAsync();
        stored.ExpiresAt.Should().Be(expires);
        stored.ExpiresAt!.Value.Kind.Should().Be(DateTimeKind.Utc);

        await using var bad = PersistenceTestDb.Widgets(mysql, tenant);
        bad.Widgets.Add(new Widget(Code(), "Local") { ExpiresAt = new DateTime(2027, 1, 2, 3, 4, 5, DateTimeKind.Local) });
        var act = () => bad.SaveChangesAsync();
        (await act.Should().ThrowAsync<DbUpdateException>()).WithInnerException<InvalidOperationException>().WithMessage("*UTC*");
    }

    [Fact]
    public void Model_WithDecimalWithoutDeclaredPrecision_RefusesToBuild()
    {
        using var context = PersistenceTestDb.Priced(mysql);

        var act = () => context.Model;

        act.Should().Throw<InvalidOperationException>().WithMessage("*Priced.Price*");
    }
}
