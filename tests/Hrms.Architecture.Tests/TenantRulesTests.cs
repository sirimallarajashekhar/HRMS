using FluentAssertions;
using Hrms.Core.Infrastructure;
using Hrms.Core.Infrastructure.Persistence;
using Hrms.Persistence;
using Hrms.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Hrms.Architecture.Tests;

public class TenantRulesTests
{
    // --- the filter-bypass scan -------------------------------------------------------------------------------------

    [Fact]
    public void ProductSource_DoesNotBypassTheTenantFilter()
    {
        var violations = TenantBypassRules.FindViolations(SolutionModel.RepoRoot());

        violations.Should().BeEmpty();
    }

    [Theory]
    [InlineData("var rows = db.Widgets.IgnoreQueryFilters().ToList();", "IgnoreQueryFilters")]
    [InlineData("db.Widgets.FromSqlRaw(\"select * from widget\");", "EF raw SQL")]
    [InlineData("db.Database.ExecuteSqlInterpolated($\"delete from widget\");", "EF raw SQL")]
    [InlineData("db.Database.SqlQuery<int>($\"select 1\");", "EF raw SQL")]
    [InlineData("await using var c = new MySqlConnection(cs);", "Direct MySQL")]
    [InlineData("using Dapper;", "Direct MySQL")]
    [InlineData("var source = new MySqlDataSourceBuilder(cs).Build(); MySqlDataSource x;", "Direct MySQL")]
    public void BypassScanner_FlagsEachForbiddenApi(string source, string expectedRule)
    {
        var violations = TenantBypassRules.FindViolationsInSource("src/Modules/Core/Application/Handler.cs", source, TenantBypassRules.Rules);

        violations.Should().ContainSingle().Which.Should().Contain(expectedRule);
    }

    [Fact]
    public void BypassScanner_IgnoresMentionsInComments()
    {
        const string source = "// never call IgnoreQueryFilters() here\n/* FromSqlRaw is forbidden */\nvar x = 1;";

        TenantBypassRules.FindViolationsInSource("src/Modules/Core/Application/Handler.cs", source, TenantBypassRules.Rules)
            .Should().BeEmpty();
    }

    [Fact]
    public void BypassScanner_AllowsTheTenantBoundConnectionInThePersistenceProject()
    {
        const string source = "using Dapper;\nDbDataSource x;";

        TenantBypassRules.FindViolationsInSource("src/Platform/Hrms.Persistence/Sql/TenantDbConnection.cs", source, TenantBypassRules.Rules)
            .Should().BeEmpty();
    }

    [Fact]
    public void BypassScanner_DoesNotAllowIgnoreQueryFiltersEvenInThePersistenceProject() =>
        TenantBypassRules.FindViolationsInSource(
                "src/Platform/Hrms.Persistence/TenantDbContext.cs", "q.IgnoreQueryFilters();", TenantBypassRules.Rules)
            .Should().ContainSingle();

    // --- the model rules --------------------------------------------------------------------------------------------

    private static CoreDbContext CoreContext()
    {
        var options = (DbContextOptions<CoreDbContext>)CoreInfrastructureExtensions
            .UseCoreMySql(new DbContextOptionsBuilder<CoreDbContext>(), "Server=127.0.0.1;Database=unused;User ID=x;Password=x")
            .Options;
        return new CoreDbContext(options, new TenantContext(), new SystemActorContext(), TimeProvider.System);
    }

    [Fact]
    public void CoreModel_EveryTenantEntityIsFilteredAndIndexedWithTenantIdFirst()
    {
        using var context = CoreContext();

        TenantModelRule.FindViolations(context.Model).Should().BeEmpty();
    }

    [Fact]
    public void CoreModel_MapsEveryConcreteTenantEntityDeclaredInTheCoreDomain()
    {
        using var context = CoreContext();
        var declared = SolutionModel.Layer("Core", "Domain").GetTypes()
            .Where(t => !t.IsAbstract && t.IsAssignableTo(typeof(TenantEntity)))
            .ToArray();

        declared.Should().NotBeEmpty();
        declared.Where(t => context.Model.FindEntityType(t) is null).Should().BeEmpty(
            "a tenant entity that no DbContext maps is either dead code or about to be persisted without the tenant filter");
    }

    [Fact]
    public void CoreModel_TenantsTableIsTheOnlyUnfilteredEntity()
    {
        using var context = CoreContext();

        var unfiltered = context.Model.GetEntityTypes()
            .Where(e => !e.ClrType.IsAssignableTo(typeof(TenantEntity)))
            .Select(e => e.ClrType.Name);

        unfiltered.Should().Equal("Tenant");
    }

    private sealed class Unindexed : TenantEntity;

    private sealed class UnindexedContext(DbContextOptions<UnindexedContext> options)
        : TenantDbContext(options, new TenantContext(), new SystemActorContext(), TimeProvider.System)
    {
        public DbSet<Unindexed> Things => Set<Unindexed>();
    }

    [Fact]
    public void ModelRule_WhenATenantEntityHasNoTenantLeadingIndex_ReportsIt()
    {
        var options = new DbContextOptionsBuilder<UnindexedContext>()
            .UseMySql("Server=127.0.0.1;Database=unused;User ID=x;Password=x", new MySqlServerVersion(new Version(8, 4, 0)))
            .Options;
        using var context = new UnindexedContext(options);

        var violations = TenantModelRule.FindViolations(context.Model);

        violations.Should().ContainSingle().Which.Should().Contain("no index leads with tenant_id");
    }
}
