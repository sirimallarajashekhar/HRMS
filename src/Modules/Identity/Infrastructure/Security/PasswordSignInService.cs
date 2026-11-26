using System.Security.Cryptography;
using System.Text;
using Hrms.Contracts;
using Hrms.Identity.Application;
using Hrms.Identity.Domain.Security;
using Hrms.Identity.Domain.Users;
using Hrms.Identity.Infrastructure.Persistence;
using Hrms.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Hrms.Identity.Infrastructure.Security;

/// <summary>
/// The sign-in rules. Two properties matter more than the rest: every failure looks the same from outside (and takes about as long),
/// and anything doubtful fails closed. In order: find the active tenant by code, find the account in that tenant, check the password
/// even when the account is locked or disabled (so the response time does not reveal that), count a wrong password against the
/// account lockout, refuse any account that holds an MFA-required role until the MFA flow exists, then record the success.
/// </summary>
public sealed partial class PasswordSignInService(
    IdentityDbContext db,
    ITenantLookup tenantLookup,
    TenantContext tenantContext,
    IPasswordHasher passwordHasher,
    TimeProvider timeProvider,
    ILogger<PasswordSignInService> logger) : IPasswordSignInService
{
    private const int ConcurrencyRetries = 3;

    public async Task<Result<SignedInUser>> SignInAsync(string tenantCode, string email, string password, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenantCode);
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(password);

        var tenantId = await tenantLookup.FindActiveTenantIdByCodeAsync(tenantCode, cancellationToken);
        var normalisedEmail = UserAccount.NormaliseEmail(email);
        if (tenantId is null || normalisedEmail is null)
        {
            return RefuseAfterDummyWork(password);
        }

        EnterTenant(tenantId.Value);

        var user = await db.Users.SingleOrDefaultAsync(u => u.Email == normalisedEmail, cancellationToken);
        if (user is null)
        {
            return RefuseAfterDummyWork(password);
        }

        // Always verify, even for a locked or disabled account, so those refusals take as long as a wrong password.
        var check = passwordHasher.Verify(user.PasswordHash, password);
        var now = timeProvider.GetUtcNow().UtcDateTime;

        if (!user.CanSignIn(now))
        {
            LogRefused(logger, "unavailable");
            return Result.Failure<SignedInUser>(SignInErrors.InvalidCredentials);
        }

        if (check == PasswordCheck.Failed)
        {
            await RegisterFailureAsync(user.Id, now, cancellationToken);
            return Result.Failure<SignedInUser>(SignInErrors.InvalidCredentials);
        }

        var roles = await LoadRolesAsync(user.Id, cancellationToken);
        if (roles.Any(role => role.RequiresMfa))
        {
            // The password was right but the account needs a second factor, which cannot be given yet: fail closed.
            LogRefused(logger, "mfa_required");
            return Result.Failure<SignedInUser>(SignInErrors.MfaRequired);
        }

        user.RegisterSuccessfulSignIn(now);
        if (check == PasswordCheck.SuccessRehashNeeded)
        {
            user.ReplacePasswordHash(passwordHasher.Hash(password));
        }

        await db.SaveChangesAsync(cancellationToken);
        return Result.Success(new SignedInUser(user.Id, tenantId.Value, user.SecurityStamp, [.. roles.Select(role => role.Code)]));
    }

    public async Task<Result<SignedInUser>> RevalidateAsync(Guid tenantId, Guid userId, string securityStamp, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(securityStamp);

        EnterTenant(tenantId);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;

        if (user is null || !user.CanSignIn(now) || !StampsEqual(user.SecurityStamp, securityStamp))
        {
            return Result.Failure<SignedInUser>(SignInErrors.SessionRevoked);
        }

        var roles = await LoadRolesAsync(user.Id, cancellationToken);
        if (roles.Any(role => role.RequiresMfa))
        {
            return Result.Failure<SignedInUser>(SignInErrors.MfaRequired);
        }

        return Result.Success(new SignedInUser(user.Id, tenantId, user.SecurityStamp, [.. roles.Select(role => role.Code)]));
    }

    private Result<SignedInUser> RefuseAfterDummyWork(string password)
    {
        passwordHasher.Verify(passwordHasher.DummyHash, password);
        return Result.Failure<SignedInUser>(SignInErrors.InvalidCredentials);
    }

    private void EnterTenant(Guid tenantId)
    {
        var id = new TenantId(tenantId);
        if (tenantContext.TenantId is { } current)
        {
            if (current != id)
            {
                throw new InvalidOperationException("Sign-in cannot switch the tenant of a request that already has one.");
            }

            return;
        }

        tenantContext.Set(id);
    }

    private async Task RegisterFailureAsync(Guid userId, DateTime now, CancellationToken cancellationToken)
    {
        var policy = await LockoutPolicyAsync(DateOnly.FromDateTime(now), cancellationToken);

        for (var attempt = 0; attempt < ConcurrencyRetries; attempt++)
        {
            var user = await db.Users.SingleAsync(u => u.Id == userId, cancellationToken);
            var outcome = user.RegisterFailedAttempt(now, policy);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                LogRefused(logger, outcome.LockedNow ? "locked" : "wrong_password");
                return;
            }
            catch (DbUpdateConcurrencyException)
            {
                // Guesses arriving in parallel update the same row; reload and count again so none is lost.
                db.ChangeTracker.Clear();
            }
        }

        LogRefused(logger, "wrong_password");
    }

    private async Task<LockoutPolicy> LockoutPolicyAsync(DateOnly today, CancellationToken cancellationToken)
    {
        var policy = await db.SecurityPolicies.AsNoTracking().ValidOn(today)
            .Select(p => new { p.MaxFailedAttempts, p.LockoutMinutes })
            .FirstOrDefaultAsync(cancellationToken);
        return policy is null ? LockoutPolicy.Default : new LockoutPolicy(policy.MaxFailedAttempts, TimeSpan.FromMinutes(policy.LockoutMinutes));
    }

    private async Task<List<RoleInfo>> LoadRolesAsync(Guid userId, CancellationToken cancellationToken) =>
        await db.UserRoleScopes.AsNoTracking()
            .Where(scope => scope.UserId == userId)
            .Join(db.Roles.AsNoTracking(), scope => scope.RoleId, role => role.Id, (scope, role) => new RoleInfo(role.Code, role.RequiresMfa))
            .Distinct()
            .ToListAsync(cancellationToken);

    private static bool StampsEqual(string stored, string presented) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(stored), Encoding.UTF8.GetBytes(presented));

    private sealed record RoleInfo(string Code, bool RequiresMfa);

    [LoggerMessage(Level = LogLevel.Information, Message = "Sign-in refused ({Reason})")]
    private static partial void LogRefused(ILogger logger, string reason);
}
