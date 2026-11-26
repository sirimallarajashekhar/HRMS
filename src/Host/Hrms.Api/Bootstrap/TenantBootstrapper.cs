using Hrms.Core.Domain.Tenants;
using Hrms.Core.Infrastructure.Persistence;
using Hrms.Identity.Infrastructure;
using Hrms.Identity.Infrastructure.Security;
using Hrms.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hrms.Api.Bootstrap;

/// <summary>What the operator supplies to create a tenant; the password comes separately and is never part of a command line.</summary>
public sealed record BootstrapRequest(string TenantCode, string TenantName, string AdminEmail, string AdminName);

public sealed record BootstrapOutcome(Guid TenantId, bool TenantCreated, Guid? AdminId, bool AdminCreated);

/// <summary>
/// Creates a tenant with everything it needs to be usable: the tenant row, the permission catalogue, the six default roles, the
/// first-party OAuth clients and the first administrator. Every step is idempotent, so a failed run can simply be repeated. The
/// administrator must change the password and, holding an MFA role, can only enrol an authenticator until they have.
/// User and role management screens arrive with the admin app; until then this is how a tenant gets its first user.
/// </summary>
public static class TenantBootstrapper
{
    public static async Task<Result<BootstrapOutcome>> RunAsync(
        IServiceProvider services, BootstrapRequest request, string adminPassword, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(adminPassword);

        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var time = provider.GetRequiredService<TimeProvider>();

        var tenant = await FindOrCreateTenantAsync(provider.GetRequiredService<CoreDbContext>(), request, time, cancellationToken);
        if (tenant.IsFailure)
        {
            return Result.Failure<BootstrapOutcome>(tenant.Error);
        }

        // From here on every identity query is filtered to this tenant.
        provider.GetRequiredService<TenantContext>().Set(tenant.Value.Tenant.TenantId);

        var seeder = provider.GetRequiredService<TenantIdentitySeeder>();
        await seeder.SyncPermissionCatalogueAsync(cancellationToken);
        await seeder.SeedDefaultRolesAsync(cancellationToken);
        await provider.GetRequiredService<OAuthClientSeeder>().EnsureFirstPartyClientsAsync(cancellationToken);

        var admin = await seeder.CreateTenantAdminAsync(request.AdminEmail, request.AdminName, adminPassword, cancellationToken);
        if (admin.IsFailure && admin.Error.Code != "identity.user.email_taken")
        {
            return Result.Failure<BootstrapOutcome>(admin.Error);
        }

        return Result.Success(new BootstrapOutcome(
            tenant.Value.Tenant.Id, tenant.Value.Created, admin.IsSuccess ? admin.Value : null, AdminCreated: admin.IsSuccess));
    }

    private static async Task<Result<(Tenant Tenant, bool Created)>> FindOrCreateTenantAsync(
        CoreDbContext db, BootstrapRequest request, TimeProvider time, CancellationToken cancellationToken)
    {
        var created = Tenant.Create(request.TenantCode, request.TenantName, time.GetUtcNow().UtcDateTime);
        if (created.IsFailure)
        {
            return Result.Failure<(Tenant, bool)>(created.Error);
        }

        var existing = await db.Tenants.SingleOrDefaultAsync(t => t.Code == created.Value.Code, cancellationToken);
        if (existing is not null)
        {
            return Result.Success((existing, false));
        }

        db.Tenants.Add(created.Value);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success((created.Value, true));
    }
}
