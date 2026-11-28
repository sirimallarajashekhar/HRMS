using System.Security.Cryptography;
using Hrms.Identity.Application;
using Hrms.Identity.Domain.Mfa;
using Hrms.Identity.Domain.Users;
using Hrms.Identity.Infrastructure.Persistence;
using Hrms.Persistence;
using Hrms.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Hrms.Identity.Infrastructure.Security;

/// <summary>
/// Self-service account operations. All of them read through <see cref="IdentityDbContext"/>, which is filtered to the tenant of the
/// request, and take only the caller's own user id, so there is no way to name another user or another tenant's user.
/// </summary>
public sealed partial class AccountService(
    IdentityDbContext db,
    IPasswordHasher passwordHasher,
    IFieldEncryptor encryptor,
    ITenantContext tenantContext,
    SecurityPolicyReader policies,
    TimeProvider timeProvider,
    ILogger<AccountService> logger) : IAccountService
{
    public const string Issuer = "HRMS";

    public async Task<Result<MfaEnrollment>> BeginMfaEnrollmentAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await LoadAsync(userId, cancellationToken);
        if (user is null)
        {
            return Result.Failure<MfaEnrollment>(AccountErrors.UserNotFound);
        }

        var secret = Totp.NewSecret();
        var protectedSecret = encryptor.Protect(Base32.Encode(secret), MfaContexts.Secret(TenantGuid(), user.Id));
        var started = user.BeginMfaEnrollment(protectedSecret);
        if (started.IsFailure)
        {
            return Result.Failure<MfaEnrollment>(started.Error);
        }

        await db.SaveChangesAsync(cancellationToken);
        return Result.Success(new MfaEnrollment(Base32.Encode(secret), Totp.ProvisioningUri(Issuer, user.Email, secret)));
    }

    public async Task<Result<IReadOnlyList<string>>> ConfirmMfaEnrollmentAsync(Guid userId, string code, CancellationToken cancellationToken)
    {
        var user = await LoadAsync(userId, cancellationToken);
        if (user is null)
        {
            return Result.Failure<IReadOnlyList<string>>(AccountErrors.UserNotFound);
        }

        if (!user.HasPendingMfaEnrollment)
        {
            return Result.Failure<IReadOnlyList<string>>(Error.Conflict("identity.mfa.no_pending_enrollment", "Start MFA enrolment first."));
        }

        var step = VerifyCode(user, code, lastUsedStep: null);
        if (step is null)
        {
            return Result.Failure<IReadOnlyList<string>>(AccountErrors.WrongCode);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var confirmed = user.ConfirmMfaEnrollment(step.Value, now);
        if (confirmed.IsFailure)
        {
            return Result.Failure<IReadOnlyList<string>>(confirmed.Error);
        }

        await RetireRecoveryCodesAsync(user.Id, cancellationToken);
        var codes = RecoveryCodes.Generate();
        foreach (var recovery in codes)
        {
            db.RecoveryCodes.Add(RecoveryCode.Create(user.Id, RecoveryCodes.Hash(RecoveryCodes.Normalise(recovery))));
        }

        await db.SaveChangesAsync(cancellationToken);
        return Result.Success(codes);
    }

    public async Task<Result> DisableMfaAsync(Guid userId, string password, string code, CancellationToken cancellationToken)
    {
        var user = await LoadAsync(userId, cancellationToken);
        if (user is null)
        {
            return Result.Failure(AccountErrors.UserNotFound);
        }

        if (IsLockedOut(user))
        {
            return Result.Failure(AccountErrors.LockedOut);
        }

        if (!user.MfaEnabled)
        {
            return Result.Failure(AccountErrors.MfaNotEnabled);
        }

        // Every refusal that does not depend on what was typed comes before the password is checked. After the check each outcome
        // saves (a counted failure or the change), so while nothing can be saved, a right password does not show.
        var requiredByRole = await db.UserRoleScopes.AsNoTracking()
            .Where(scope => scope.UserId == user.Id)
            .Join(db.Roles.AsNoTracking(), scope => scope.RoleId, role => role.Id, (scope, role) => role.RequiresMfa)
            .AnyAsync(required => required, cancellationToken);
        if (requiredByRole)
        {
            return Result.Failure(AccountErrors.MfaRequiredByRole);
        }

        if (passwordHasher.Verify(user.PasswordHash, password) == PasswordCheck.Failed)
        {
            return Result.Failure(await RegisterFailureAsync(user, SecurityEvents.Password, AccountErrors.WrongPassword, cancellationToken));
        }

        if (VerifyCode(user, code, user.MfaLastUsedStep) is null)
        {
            return Result.Failure(await RegisterFailureAsync(user, SecurityEvents.Totp, AccountErrors.WrongCode, cancellationToken));
        }

        user.DisableMfa();
        await RetireRecoveryCodesAsync(user.Id, cancellationToken);
        return await SaveCredentialChangeAsync(user.Id, cancellationToken);
    }

    public async Task<Result> ChangePasswordAsync(Guid userId, string currentPassword, string newPassword, CancellationToken cancellationToken)
    {
        var user = await LoadAsync(userId, cancellationToken);
        if (user is null)
        {
            return Result.Failure(AccountErrors.UserNotFound);
        }

        // Checked before the password: a locked account learns nothing from a guess, and a change would lift the lock.
        if (IsLockedOut(user))
        {
            return Result.Failure(AccountErrors.LockedOut);
        }

        // As in DisableMfaAsync: the new password is judged on what was sent, before the current one is checked. Comparing the new
        // password with the stored hash here would refuse a right guess without saving anything.
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var violations = (await policies.PasswordAsync(DateOnly.FromDateTime(now), cancellationToken)).Validate(newPassword);
        if (violations.Count > 0)
        {
            return Result.Failure(AccountErrors.PasswordPolicy(violations));
        }

        if (string.Equals(newPassword, currentPassword, StringComparison.Ordinal))
        {
            return Result.Failure(Error.Validation("identity.account.password_unchanged", "The new password must differ from the current one."));
        }

        if (passwordHasher.Verify(user.PasswordHash, currentPassword) == PasswordCheck.Failed)
        {
            return Result.Failure(await RegisterFailureAsync(user, SecurityEvents.Password, AccountErrors.WrongPassword, cancellationToken));
        }

        user.ChangePassword(passwordHasher.Hash(newPassword), now);
        return await SaveCredentialChangeAsync(user.Id, cancellationToken);
    }

    private Task<UserAccount?> LoadAsync(Guid userId, CancellationToken cancellationToken) =>
        db.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);

    private Guid TenantGuid() => tenantContext.Require().Value;

    /// <summary>The matched time step, or null for a wrong, malformed or replayed code (or an unreadable stored secret).</summary>
    private long? VerifyCode(UserAccount user, string code, long? lastUsedStep)
    {
        if (user.MfaSecretProtected is null)
        {
            return null;
        }

        string secretText;
        try
        {
            secretText = encryptor.Unprotect(user.MfaSecretProtected, MfaContexts.Secret(TenantGuid(), user.Id));
        }
        catch (CryptographicException)
        {
            return null;
        }

        var secret = Base32.TryDecode(secretText);
        return secret is null ? null : Totp.Verify(secret, code?.Trim(), timeProvider.GetUtcNow(), lastUsedStep);
    }

    private bool IsLockedOut(UserAccount user) => user.IsLockedOut(timeProvider.GetUtcNow().UtcDateTime);

    /// <summary>
    /// Saves a change that a right password allowed. If parallel wrong guesses changed the account first, nothing is written: the
    /// answer is the lock if they caused one, as a wrong guess would get, and otherwise a conflict the caller can retry.
    /// </summary>
    private async Task<Result> SaveCredentialChangeAsync(Guid userId, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return Result.Success();
        }
        catch (Exception ex) when (WriteConflicts.Is(ex))
        {
            db.ChangeTracker.Clear();
            var current = await LoadAsync(userId, cancellationToken);
            return Result.Failure(current is not null && IsLockedOut(current) ? AccountErrors.LockedOut : AccountErrors.ConcurrentChange);
        }
    }

    /// <returns>The error to answer with: <paramref name="refusal"/>, or the lock if another request had locked the account first.</returns>
    private async Task<Error> RegisterFailureAsync(UserAccount user, string factor, Error refusal, CancellationToken cancellationToken)
    {
        // Guessing the password or a code through a stolen session counts toward the same lockout as guessing at sign-in. A database
        // failure other than a lost race is deliberately not caught: the request fails, as a correct guess does, because every
        // path after a right password saves. Keep it so when adding a refusal.
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var policy = await policies.LockoutAsync(DateOnly.FromDateTime(now), cancellationToken);
        var outcome = await FailedAttempts.RecordAsync(
            db, user.Id, now, policy, SecurityEvents.DuringAccountChange, factor, cancellationToken);
        if (outcome is null)
        {
            LogAttemptNotRecorded(logger);
        }

        return outcome is { Counted: false } ? AccountErrors.LockedOut : refusal;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "A wrong password or code could not be counted or logged: every try lost a race")]
    private static partial void LogAttemptNotRecorded(ILogger logger);

    private async Task RetireRecoveryCodesAsync(Guid userId, CancellationToken cancellationToken)
    {
        var existing = await db.RecoveryCodes.Where(c => c.UserId == userId).ToListAsync(cancellationToken);
        db.RecoveryCodes.RemoveRange(existing);
    }
}
