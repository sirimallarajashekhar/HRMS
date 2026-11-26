using FluentAssertions;
using Hrms.Persistence.Sql;
using Hrms.SharedKernel;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;

namespace Hrms.Core.IntegrationTests.Persistence;

public sealed class WidgetRow
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public long RowVersion { get; set; }

    public bool IsDeleted { get; set; }
}

[Collection(MySqlTestGroup.Name)]
public class TenantDbConnectionTests(MySqlFixture mysql) : IAsyncLifetime
{
    private static readonly DateTime Created = new(2026, 11, 25, 10, 30, 0, DateTimeKind.Utc);

    private readonly List<MySqlDataSource> _dataSources = [];

    public Task InitializeAsync() => PersistenceTestDb.EnsureSchemaAsync(mysql);

    public async Task DisposeAsync()
    {
        foreach (var dataSource in _dataSources)
        {
            await dataSource.DisposeAsync();
        }
    }

    private TenantDbConnectionFactory Factory(TenantId? tenant)
    {
        var connectionString = new MySqlConnectionStringBuilder(mysql.ConnectionString)
        {
            GuidFormat = MySqlGuidFormat.Binary16,
            DateTimeKind = MySqlDateTimeKind.Utc,
        }.ConnectionString;
        var dataSource = new MySqlDataSourceBuilder(connectionString).Build();
        _dataSources.Add(dataSource);

        var context = new TenantContext();
        if (tenant is { } id)
        {
            context.Set(id);
        }

        return new TenantDbConnectionFactory(dataSource, context);
    }

    private async Task<Guid> SeedViaEfAsync(TenantId tenant, string name = "Seed")
    {
        await using var context = PersistenceTestDb.Widgets(mysql, tenant, new FixedTimeProvider(new DateTimeOffset(Created)));
        var widget = new Widget(Guid.NewGuid().ToString("N")[..12], name);
        context.Widgets.Add(widget);
        await context.SaveChangesAsync();
        return widget.Id;
    }

    private async Task<Widget?> ReadViaEfAsync(TenantId tenant, Guid id)
    {
        await using var context = PersistenceTestDb.Widgets(mysql, tenant);
        return await context.Widgets.AsNoTracking().SingleOrDefaultAsync(w => w.Id == id);
    }

    [Fact]
    public async Task Query_AsTenantA_ReturnsOnlyTenantARowsMappedFromSnakeCase()
    {
        var a = PersistenceTestDb.NewTenant();
        var b = PersistenceTestDb.NewTenant();
        var aId = await SeedViaEfAsync(a, "mine");
        var bId = await SeedViaEfAsync(b, "theirs");

        await using var connection = await Factory(a).OpenAsync();
        var rows = await connection.QueryAsync<WidgetRow>(
            "SELECT id, tenant_id, code, name, created_at, row_version, is_deleted FROM widget WHERE tenant_id = @TenantId AND is_deleted = 0");

        rows.Should().ContainSingle();
        var row = rows[0];
        row.Id.Should().Be(aId).And.NotBe(bId);
        row.TenantId.Should().Be(a.Value);
        row.Name.Should().Be("mine");
        row.RowVersion.Should().Be(1);
        row.IsDeleted.Should().BeFalse();
        row.CreatedAt.Should().Be(Created);
        row.CreatedAt.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public async Task Query_ForAnotherTenantsIdByPrimaryKey_ReturnsNothing()
    {
        var a = PersistenceTestDb.NewTenant();
        var b = PersistenceTestDb.NewTenant();
        var bId = await SeedViaEfAsync(b);

        await using var connection = await Factory(a).OpenAsync();
        var row = await connection.QuerySingleOrDefaultAsync<WidgetRow>(
            "SELECT id, tenant_id, code, name, created_at, row_version, is_deleted FROM widget WHERE tenant_id = @TenantId AND id = @Id",
            new { Id = bId });

        row.Should().BeNull();
    }

    [Fact]
    public async Task Open_WithoutATenantInContext_ThrowsTenantNotResolved()
    {
        var factory = Factory(tenant: null);

        var act = async () => await factory.OpenAsync();

        await act.Should().ThrowAsync<TenantNotResolvedException>();
    }

    [Fact]
    public async Task Update_OfAnotherTenantsRow_AffectsNothingAndLeavesItUntouched()
    {
        var a = PersistenceTestDb.NewTenant();
        var b = PersistenceTestDb.NewTenant();
        var id = await SeedViaEfAsync(a, "original");

        await using (var asB = await Factory(b).OpenAsync())
        {
            var affected = await asB.ExecuteAsync(
                "UPDATE widget SET name = @Name WHERE tenant_id = @TenantId AND id = @Id",
                new { Id = id, Name = "hijacked" });
            affected.Should().Be(0);
        }

        (await ReadViaEfAsync(a, id))!.Name.Should().Be("original");
    }

    [Fact]
    public async Task Delete_OfAnotherTenantsRow_AffectsNothingAndTheRowSurvives()
    {
        var a = PersistenceTestDb.NewTenant();
        var b = PersistenceTestDb.NewTenant();
        var id = await SeedViaEfAsync(a);

        await using (var asB = await Factory(b).OpenAsync())
        {
            var affected = await asB.ExecuteAsync("DELETE FROM widget WHERE tenant_id = @TenantId AND id = @Id", new { Id = id });
            affected.Should().Be(0);
        }

        (await ReadViaEfAsync(a, id)).Should().NotBeNull();
    }

    [Fact]
    public async Task Update_OfOwnRow_ChangesIt()
    {
        var a = PersistenceTestDb.NewTenant();
        var id = await SeedViaEfAsync(a, "before");

        await using (var asA = await Factory(a).OpenAsync())
        {
            (await asA.ExecuteAsync("UPDATE widget SET name = @Name WHERE tenant_id = @TenantId AND id = @Id", new { Id = id, Name = "after" }))
                .Should().Be(1);
        }

        (await ReadViaEfAsync(a, id))!.Name.Should().Be("after");
    }

    [Fact]
    public async Task Insert_TakesTheTenantFromTheContextAndIsVisibleOnlyToThatTenantThroughEf()
    {
        var a = PersistenceTestDb.NewTenant();
        var b = PersistenceTestDb.NewTenant();
        var id = Guid.CreateVersion7();

        await using (var asA = await Factory(a).OpenAsync())
        {
            (await asA.ExecuteAsync(
                "INSERT INTO widget (id, tenant_id, code, name, created_at, is_deleted, row_version) "
                + "VALUES (@Id, @TenantId, @Code, @Name, @CreatedAt, 0, 1)",
                new { Id = id, Code = "dapper", Name = "inserted", CreatedAt = Created })).Should().Be(1);
        }

        var seenByA = await ReadViaEfAsync(a, id);
        seenByA.Should().NotBeNull();
        seenByA!.TenantId.Should().Be(a);
        (await ReadViaEfAsync(b, id)).Should().BeNull();
    }

    [Theory]
    [InlineData("TenantId")]
    [InlineData("tenantid")]
    [InlineData("TENANTID")]
    public async Task Query_WhenTheCallerSuppliesATenantParameter_IsRefused(string name)
    {
        var a = PersistenceTestDb.NewTenant();
        var b = PersistenceTestDb.NewTenant();
        await using var connection = await Factory(a).OpenAsync();

        var act = () => connection.QueryAsync<WidgetRow>(
            "SELECT id, tenant_id FROM widget WHERE tenant_id = @TenantId",
            new Dictionary<string, object> { [name] = b.Value });

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*cannot be supplied by the caller*");
    }

    [Fact]
    public async Task Query_WhenTheCallerSuppliesATenantParameterInAnAnonymousObject_IsRefused()
    {
        var a = PersistenceTestDb.NewTenant();
        var b = PersistenceTestDb.NewTenant();
        await using var connection = await Factory(a).OpenAsync();

        var act = () => connection.QueryAsync<WidgetRow>(
            "SELECT id, tenant_id FROM widget WHERE tenant_id = @TenantId",
            new { TenantId = b.Value });

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*cannot be supplied by the caller*");
    }

    [Fact]
    public async Task Query_WithADapperParameterBag_IsRefusedBecauseItCanHideATenantValue()
    {
        var a = PersistenceTestDb.NewTenant();
        await using var connection = await Factory(a).OpenAsync();
        var bag = new Dapper.DynamicParameters(new { TenantId = Guid.NewGuid() });

        var act = () => connection.QueryAsync<WidgetRow>("SELECT id, tenant_id FROM widget WHERE tenant_id = @TenantId", bag);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*parameter bag*");
    }

    [Fact]
    public async Task Query_WithAListParameter_ExpandsItAndStaysInsideTheTenant()
    {
        var a = PersistenceTestDb.NewTenant();
        var b = PersistenceTestDb.NewTenant();
        var aId = await SeedViaEfAsync(a);
        var bId = await SeedViaEfAsync(b);

        await using var connection = await Factory(a).OpenAsync();
        var rows = await connection.QueryAsync<WidgetRow>(
            "SELECT id, tenant_id FROM widget WHERE tenant_id = @TenantId AND id IN @Ids",
            new { Ids = new[] { aId, bId } });

        rows.Should().ContainSingle().Which.Id.Should().Be(aId);
    }

    [Fact]
    public async Task Query_WithNoParameters_RunsWithTheTenantBound()
    {
        var a = PersistenceTestDb.NewTenant();
        await SeedViaEfAsync(a);
        await using var connection = await Factory(a).OpenAsync();

        var rows = await connection.QueryAsync<WidgetRow>("SELECT id, tenant_id FROM widget WHERE tenant_id = @TenantId");

        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Query_WithoutATenantPredicate_IsRefusedBeforeItRuns()
    {
        var a = PersistenceTestDb.NewTenant();
        await SeedViaEfAsync(a);
        await using var connection = await Factory(a).OpenAsync();

        var act = () => connection.QueryAsync<WidgetRow>("SELECT id, tenant_id FROM widget");

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*Unsafe tenant SQL*");
    }

    [Fact]
    public async Task Queries_RunningInParallelForDifferentTenants_EachSeeOnlyTheirOwnRows()
    {
        var tenants = Enumerable.Range(0, 12).Select(_ => PersistenceTestDb.NewTenant()).ToArray();
        foreach (var tenant in tenants)
        {
            await SeedViaEfAsync(tenant);
            await SeedViaEfAsync(tenant);
        }

        var results = await Task.WhenAll(tenants.Select(async tenant =>
        {
            await using var connection = await Factory(tenant).OpenAsync();
            var rows = await connection.QueryAsync<WidgetRow>("SELECT id, tenant_id FROM widget WHERE tenant_id = @TenantId");
            return (tenant, rows);
        }));

        foreach (var (tenant, rows) in results)
        {
            rows.Should().HaveCount(2);
            rows.Should().OnlyContain(r => r.TenantId == tenant.Value);
        }
    }
}
