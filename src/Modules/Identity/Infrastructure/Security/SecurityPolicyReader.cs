using Hrms.Identity.Domain.Security;
using Hrms.Identity.Infrastructure.Persistence;
using Hrms.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Hrms.Identity.Infrastructure.Security;

/// <summary>Reads the password and lockout policy in force for the current tenant on a date, falling back to the defaults.</summary>
public sealed class SecurityPolicyReader(IdentityDbContext db)
{
    public async Task<LockoutPolicy> LockoutAsync(DateOnly today, CancellationToken cancellationToken)
    {
        var policy = await db.SecurityPolicies.AsNoTracking().ValidOn(today)
            .Select(p => new { p.MaxFailedAttempts, p.LockoutMinutes })
            .FirstOrDefaultAsync(cancellationToken);
        return policy is null ? LockoutPolicy.Default : new LockoutPolicy(policy.MaxFailedAttempts, TimeSpan.FromMinutes(policy.LockoutMinutes));
    }

    public async Task<PasswordPolicy> PasswordAsync(DateOnly today, CancellationToken cancellationToken)
    {
        var policy = await db.SecurityPolicies.AsNoTracking().ValidOn(today)
            .Select(p => new { p.MinPasswordLength, p.RequireUppercase, p.RequireLowercase, p.RequireDigit, p.RequireSymbol })
            .FirstOrDefaultAsync(cancellationToken);
        return policy is null
            ? PasswordPolicy.Default
            : new PasswordPolicy(policy.MinPasswordLength, policy.RequireUppercase, policy.RequireLowercase, policy.RequireDigit, policy.RequireSymbol);
    }
}
