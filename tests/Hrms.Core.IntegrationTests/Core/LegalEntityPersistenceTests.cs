using FluentAssertions;
using Hrms.Core.Domain.LegalEntities;
using Hrms.Core.IntegrationTests.Persistence;
using Hrms.Persistence;
using Hrms.Persistence.Sql;
using Hrms.SharedKernel;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;

namespace Hrms.Core.IntegrationTests.Core;

public sealed class LegalEntityRow
{
    public Guid Id { get; set; }

    public string Code { get; set; } = string.Empty;

    public string CurrencyCode { get; set; } = string.Empty;
}

[Collection(MySqlTestGroup.Name)]
public class LegalEntityPersistenceTests(MySqlFixture mysql) : IAsyncLifetime
{
    private string _connectionString = string.Empty;

    public async Task InitializeAsync() => _connectionString = await CoreTestDb.CreateMigratedAsync(mysql);

    public Task DisposeAsync() => Task.CompletedTask;

    private static TenantId NewTenant() => PersistenceTestDb.NewTenant();

    [Fact]
    public async Task SaveAndLoad_RoundTripsEveryFieldAndStampsAudit()
    {
        var tenant = NewTenant();
        var actor = Guid.CreateVersion7();
        var entity = CoreTestDb.NewLegalEntity("ROUND-TRIP");
        await using (var context = CoreTestDb.Context(_connectionString, tenant, actor: actor))
        {
            context.LegalEntities.Add(entity);
            await context.SaveChangesAsync();
        }

        await using var verify = CoreTestDb.Context(_connectionString, tenant);
        var stored = await verify.LegalEntities.AsNoTracking().SingleAsync(e => e.Id == entity.Id);

        stored.Code.Should().Be("ROUND-TRIP");
        stored.Name.Should().Be("Acme India Pvt Ltd");
        stored.CountryCode.Should().Be("IN");
        stored.CurrencyCode.Should().Be("INR");
        stored.FiscalYearStartMonth.Should().Be(4);
        stored.CountryPackCode.Should().Be("IN");
        stored.DefaultLanguage.Should().Be("en-IN");
        stored.TimeZoneId.Should().Be("Asia/Kolkata");
        stored.TenantId.Should().Be(tenant);
        stored.CreatedAt.Should().Be(CoreTestDb.Created);
        stored.CreatedBy.Should().Be(actor);
        stored.RowVersion.Should().Be(1);
    }

    [Fact]
    public async Task Add_DuplicateCodeInTheSameTenant_IsRejectedByTheDatabase()
    {
        var tenant = NewTenant();
        await CoreTestDb.SeedLegalEntityAsync(_connectionString, tenant, "DUP");
        await using var context = CoreTestDb.Context(_connectionString, tenant);
        context.LegalEntities.Add(CoreTestDb.NewLegalEntity("dup"));

        var act = () => context.SaveChangesAsync();

        (await act.Should().ThrowAsync<DbUpdateException>()).WithInnerException<MySqlException>().Which.Message.Should().Contain("Duplicate");
    }

    [Fact]
    public async Task Add_SameCodeInAnotherTenant_IsAllowed()
    {
        await CoreTestDb.SeedLegalEntityAsync(_connectionString, NewTenant(), "SHARED");

        var act = () => CoreTestDb.SeedLegalEntityAsync(_connectionString, NewTenant(), "SHARED");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Add_CodeOfASoftDeletedEntity_IsAllowedAgainButOnlyOnceMore()
    {
        var tenant = NewTenant();
        var first = await CoreTestDb.SeedLegalEntityAsync(_connectionString, tenant, "REUSE");

        await using (var context = CoreTestDb.Context(_connectionString, tenant))
        {
            var stored = await context.LegalEntities.SingleAsync(e => e.Id == first.Id);
            context.LegalEntities.Remove(stored);
            await context.SaveChangesAsync();
        }

        var reuse = () => CoreTestDb.SeedLegalEntityAsync(_connectionString, tenant, "REUSE");
        await reuse.Should().NotThrowAsync();

        var duplicateOfTheLiveOne = () => CoreTestDb.SeedLegalEntityAsync(_connectionString, tenant, "REUSE");
        await duplicateOfTheLiveOne.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task Query_AsTenantA_NeverSeesTenantBsLegalEntities()
    {
        var a = NewTenant();
        var b = NewTenant();
        var mine = await CoreTestDb.SeedLegalEntityAsync(_connectionString, a, "MINE");
        var theirs = await CoreTestDb.SeedLegalEntityAsync(_connectionString, b, "THEIRS");

        await using var asA = CoreTestDb.Context(_connectionString, a);

        var visible = await asA.LegalEntities.AsNoTracking().Select(e => e.Id).ToListAsync();
        visible.Should().Equal(mine.Id);
        (await asA.LegalEntities.FindAsync(theirs.Id)).Should().BeNull();
    }

    [Fact]
    public async Task Update_OfAnotherTenantsLegalEntity_IsRefusedAndTheRowIsUntouched()
    {
        var a = NewTenant();
        var b = NewTenant();
        var entity = await CoreTestDb.SeedLegalEntityAsync(_connectionString, a, "PROTECTED");
        LegalEntity detached;
        await using (var asA = CoreTestDb.Context(_connectionString, a))
        {
            detached = await asA.LegalEntities.AsNoTracking().SingleAsync(e => e.Id == entity.Id);
        }

        await using (var asB = CoreTestDb.Context(_connectionString, b))
        {
            asB.LegalEntities.Attach(detached);
            detached.Update("Hijacked", "en-IN", "Asia/Kolkata").IsSuccess.Should().BeTrue();
            asB.Entry(detached).State = EntityState.Modified;
            var act = () => asB.SaveChangesAsync();
            await act.Should().ThrowAsync<CrossTenantWriteException>();
        }

        await using var verify = CoreTestDb.Context(_connectionString, a);
        (await verify.LegalEntities.AsNoTracking().SingleAsync(e => e.Id == entity.Id)).Name.Should().Be("Acme India Pvt Ltd");
    }

    [Fact]
    public async Task Update_OfOwnLegalEntity_PersistsAndBumpsTheRowVersion()
    {
        var tenant = NewTenant();
        var entity = await CoreTestDb.SeedLegalEntityAsync(_connectionString, tenant, "EDIT");

        await using (var context = CoreTestDb.Context(_connectionString, tenant))
        {
            var stored = await context.LegalEntities.SingleAsync(e => e.Id == entity.Id);
            stored.Update("Acme India Limited", "hi-IN", "Asia/Kolkata").IsSuccess.Should().BeTrue();
            await context.SaveChangesAsync();
        }

        await using var verify = CoreTestDb.Context(_connectionString, tenant);
        var reloaded = await verify.LegalEntities.AsNoTracking().SingleAsync(e => e.Id == entity.Id);
        reloaded.Name.Should().Be("Acme India Limited");
        reloaded.DefaultLanguage.Should().Be("hi-IN");
        reloaded.RowVersion.Should().Be(2);
    }

    [Fact]
    public async Task Dapper_AsTenantA_ReadsOnlyTenantAsLegalEntities()
    {
        var a = NewTenant();
        var b = NewTenant();
        var mine = await CoreTestDb.SeedLegalEntityAsync(_connectionString, a, "DAPPER-A");
        await CoreTestDb.SeedLegalEntityAsync(_connectionString, b, "DAPPER-B");

        var context = new TenantContext();
        context.Set(a);
        await using var dataSource = new MySqlDataSourceBuilder(MySqlConnectionStrings.ForHrms(_connectionString)).Build();
        var factory = new TenantDbConnectionFactory(dataSource, context);
        await using var connection = await factory.OpenAsync();

        var rows = await connection.QueryAsync<LegalEntityRow>(
            "SELECT id, code, currency_code FROM legal_entity WHERE tenant_id = @TenantId AND is_deleted = 0");

        rows.Should().ContainSingle().Which.Id.Should().Be(mine.Id);
        rows[0].CurrencyCode.Should().Be("INR");
    }
}
