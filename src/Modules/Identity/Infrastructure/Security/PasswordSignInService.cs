using System.Security.Cryptography;
using System.Text;
using Hrms.Contracts;
using Hrms.Identity.Application;
using Hrms.Identity.Domain.Mfa;
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
/// account lockout, then the second factor: an account with MFA must present a valid code (a wrong one counts like a wrong password),
/// and an account whose role requires MFA but has none yet gets an enrolment-only session. Only then is the success recorded.
/// </summary>
public sealed partial class PasswordSignInService(
    IdentityDbContext db,
    ITenantLookup tenantLookup,
    TenantContext tenantContext,
    IPasswordHasher passwordHasher,
    IFieldEncryptor encryptor,
    SecurityPolicyReader policies,
    TimeProvider timeProvider,
    ILogger<PasswordSignInService> logger) : IPasswordSignInService
{
    /// <summary>
    /// Parallel guesses update the same account row, so each failure is recorded with an optimistic retry. The budget is generous
    /// and jittered: a burst of N simultaneous guesses needs up to N attempts for the slowest, and losing a count would help the attacker.
    /// </summary>
    private const int ConcurrencyRetries = 30;

    public async Task<Result<SignedInUser>> SignInAsync(
        string tenantCode, string email, string password, MfaProof? mfa, CancellationToken cancellationToken)
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
            await RegisterFailureAsync(user.Id, now, SecurityEvents.Password, cancellationToken);
            return Result.Failure<SignedInUser>(SignInErrors.InvalidCredentials);
        }

        var roles = await LoadRolesAsync(user.Id, cancellationToken);
        var enrollmentOnly = false;
        string? secondFactor = null;

        if (user.MfaEnabled)
        {
            if (mfa is null || mfa.IsEmpty)
            {
                LogRefused(logger, "mfa_code_required");
                return Result.Failure<SignedInUser>(SignInErrors.MfaCodeRequired);
            }

            secondFactor = string.IsNullOrWhiteSpace(mfa.TotpCode) ? SecurityEvents.RecoveryCode : SecurityEvents.Totp;
            if (!await VerifySecondFactorAsync(user, tenantId.Value, mfa, now, cancellationToken))
            {
                await RegisterFailureAsync(user.Id, now, secondFactor, cancellationToken);
                return Result.Failure<SignedInUser>(SignInErrors.InvalidCredentials);
            }
        }
        else if (roles.Any(role => role.RequiresMfa))
        {
            // The password was right but the account must use MFA and has not set it up: it may enrol and do nothing else.
            enrollmentOnly = true;
        }

        user.RegisterSuccessfulSignIn(now);
        if (check == PasswordCheck.SuccessRehashNeeded)
        {
            user.ReplacePasswordHash(passwordHasher.Hash(password));
        }

        SecurityEvents.SignedIn(db, user, secondFactor, enrollmentOnly);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another sign-in changed the account at the same moment (for example it used the same code first): refuse, the client retries.
            return Result.Failure<SignedInUser>(SignInErrors.InvalidCredentials);
        }

        return Result.Success(new SignedInUser(
            user.Id,
            tenantId.Value,
            user.SecurityStamp,
            enrollmentOnly ? [] : [.. roles.Select(role => role.Code)],
            enrollmentOnly));
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
        if (roles.Any(role => role.RequiresMfa) && !user.MfaEnabled)
        {
            // A refresh token is only ever issued to a session that passed MFA; if MFA is gone, so is the session.
            return Result.Failure<SignedInUser>(SignInErrors.MfaRequired);
        }

        return Result.Success(new SignedInUser(user.Id, tenantId, user.SecurityStamp, [.. roles.Select(role => role.Code)]));
    }

    private async Task<bool> VerifySecondFactorAsync(UserAccount user, Guid tenantId, MfaProof mfa, DateTime now, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(mfa.TotpCode))
        {
            string secretText;
            try
            {
                secretText = encryptor.Unprotect(user.MfaSecretProtected ?? string.Empty, MfaContexts.Secret(tenantId, user.Id));
            }
            catch (CryptographicException)
            {
                LogSecretUnreadable(logger);
                return false;
            }

            var secret = Base32.TryDecode(secretText);
            var step = secret is null ? null : Totp.Verify(secret, mfa.TotpCode.Trim(), new DateTimeOffset(now, TimeSpan.Zero), user.MfaLastUsedStep);
            return step is { } accepted && user.TryAcceptMfaStep(accepted);
        }

        var normalised = RecoveryCodes.Normalise(mfa.RecoveryCode);
        if (!RecoveryCodes.LooksValid(normalised))
        {
            return false;
        }

        var hash = RecoveryCodes.Hash(normalised);
        var code = await db.RecoveryCodes.SingleOrDefaultAsync(c => c.UserId == user.Id && c.CodeHash == hash && c.UsedAt == null, cancellationToken);
        return code is not null && code.TryUse(now);
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

    private async Task RegisterFailureAsync(Guid userId, DateTime now, string factor, CancellationToken cancellationToken)
    {
        var policy = await policies.LockoutAsync(DateOnly.FromDateTime(now), cancellationToken);

        for (var attempt = 0; attempt < ConcurrencyRetries; attempt++)
        {
            var user = await db.Users.SingleAsync(u => u.Id == userId, cancellationToken);
            var outcome = user.RegisterFailedAttempt(now, policy);
            SecurityEvents.Refused(db, user, outcome, SecurityEvents.DuringSignIn, factor);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                LogRefused(logger, outcome.LockedNow ? "locked" : "wrong_credentials");
                return;
            }
            catch (DbUpdateConcurrencyException)
            {
                // Guesses arriving in parallel update the same row; reload and count again so none is lost. The random pause
                // spreads the retries out so contenders stop colliding on the same instant.
                db.ChangeTracker.Clear();
                await Task.Delay(Random.Shared.Next(1, 4 + (attempt * 2)), cancellationToken);
            }
        }

        LogRefused(logger, "wrong_credentials");
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

    [LoggerMessage(Level = LogLevel.Error, Message = "A stored MFA secret could not be decrypted; the account cannot use MFA until it is reset")]
    private static partial void LogSecretUnreadable(ILogger logger);
}
