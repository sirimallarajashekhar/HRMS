using FluentAssertions;
using Hrms.Core.Infrastructure;
using Hrms.Core.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Hrms.Core.IntegrationTests.Core;

[Collection(MySqlTestGroup.Name)]
public class TenantDirectoryTests(MySqlFixture mysql) : IAsyncLifetime
{
    private string _connectionString = string.Empty;

    public async Task InitializeAsync() => _connectionString = await CoreTestDb.CreateMigratedAsync(mysql);

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<bool> IsActiveAsync(Guid id)
    {
        await using var context = CoreTestDb.Context(_connectionString, tenant: null);
        return await new TenantDirectory(context).IsActiveAsync(id, CancellationToken.None);
    }

    [Fact]
    public async Task IsActive_ForAnActiveTenant_ReturnsTrue()
    {
        var tenant = await CoreTestDb.SeedTenantAsync(_connectionString);

        (await IsActiveAsync(tenant.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task IsActive_ForASuspendedTenant_ReturnsFalse()
    {
        var tenant = await CoreTestDb.SeedTenantAsync(_connectionString, suspended: true);

        (await IsActiveAsync(tenant.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task IsActive_ForAnUnknownTenant_ReturnsFalseJustLikeASuspendedOne() =>
        (await IsActiveAsync(Guid.CreateVersion7())).Should().BeFalse();

    [Fact]
    public async Task IsActive_AfterSuspendingAndReactivating_FollowsTheStatus()
    {
        var tenant = await CoreTestDb.SeedTenantAsync(_connectionString);

        await using (var context = CoreTestDb.Context(_connectionString, tenant: null))
        {
            var stored = await context.Tenants.SingleAsync(t => t.Id == tenant.Id);
            stored.Suspend();
            await context.SaveChangesAsync();
        }

        (await IsActiveAsync(tenant.Id)).Should().BeFalse();

        await using (var context = CoreTestDb.Context(_connectionString, tenant: null))
        {
            var stored = await context.Tenants.SingleAsync(t => t.Id == tenant.Id);
            stored.Activate();
            await context.SaveChangesAsync();
        }

        (await IsActiveAsync(tenant.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task Tenant_WithADuplicateCode_IsRejectedByTheDatabase()
    {
        await CoreTestDb.SeedTenantAsync(_connectionString, "SAME-CODE");

        var act = () => CoreTestDb.SeedTenantAsync(_connectionString, "SAME-CODE");

        await act.Should().ThrowAsync<DbUpdateException>();
    }
}
