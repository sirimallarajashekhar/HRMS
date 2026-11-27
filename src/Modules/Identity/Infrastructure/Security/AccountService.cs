using System.Security.Cryptography;
using Hrms.Identity.Application;
using Hrms.Identity.Domain.Mfa;
using Hrms.Identity.Domain.Users;
using Hrms.Identity.Infrastructure.Persistence;
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

        if (!user.MfaEnabled)
        {
            return Result.Failure(AccountErrors.MfaNotEnabled);
        }

        if (passwordHasher.Verify(user.PasswordHash, password) == PasswordCheck.Failed)
        {
            await RegisterFailureAsync(user, SecurityEvents.Password, cancellationToken);
            return Result.Failure(AccountErrors.WrongPassword);
        }

        var requiredByRole = await db.UserRoleScopes.AsNoTracking()
            .Where(scope => scope.UserId == user.Id)
            .Join(db.Roles.AsNoTracking(), scope => scope.RoleId, role => role.Id, (scope, role) => role.RequiresMfa)
            .AnyAsync(required => required, cancellationToken);
        if (requiredByRole)
        {
            return Result.Failure(AccountErrors.MfaRequiredByRole);
        }

        if (VerifyCode(user, code, user.MfaLastUsedStep) is null)
        {
            await RegisterFailureAsync(user, SecurityEvents.Totp, cancellationToken);
            return Result.Failure(AccountErrors.WrongCode);
        }

        user.DisableMfa();
        await RetireRecoveryCodesAsync(user.Id, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result> ChangePasswordAsync(Guid userId, string currentPassword, string newPassword, CancellationToken cancellationToken)
    {
        var user = await LoadAsync(userId, cancellationToken);
        if (user is null)
        {
            return Result.Failure(AccountErrors.UserNotFound);
        }

        if (passwordHasher.Verify(user.PasswordHash, currentPassword) == PasswordCheck.Failed)
        {
            await RegisterFailureAsync(user, SecurityEvents.Password, cancellationToken);
            return Result.Failure(AccountErrors.WrongPassword);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var violations = (await policies.PasswordAsync(DateOnly.FromDateTime(now), cancellationToken)).Validate(newPassword);
        if (violations.Count > 0)
        {
            return Result.Failure(AccountErrors.PasswordPolicy(violations));
        }

        if (passwordHasher.Verify(user.PasswordHash, newPassword) != PasswordCheck.Failed)
        {
            return Result.Failure(Error.Validation("identity.account.password_unchanged", "The new password must differ from the current one."));
        }

        user.ChangePassword(passwordHasher.Hash(newPassword), now);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
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

    private async Task RegisterFailureAsync(UserAccount user, string factor, CancellationToken cancellationToken)
    {
        // Guessing the password or a code through a stolen session counts toward the same lockout as guessing at sign-in. A database
        // failure other than a lost race is deliberately not caught: the request fails, as a correct guess would when it saves.
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var policy = await policies.LockoutAsync(DateOnly.FromDateTime(now), cancellationToken);
        var outcome = await FailedAttempts.RecordAsync(
            db, user.Id, now, policy, SecurityEvents.DuringAccountChange, factor, cancellationToken);
        if (outcome is null)
        {
            LogAttemptNotRecorded(logger);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "A wrong password or code could not be counted or logged: every try lost a race")]
    private static partial void LogAttemptNotRecorded(ILogger logger);

    private async Task RetireRecoveryCodesAsync(Guid userId, CancellationToken cancellationToken)
    {
        var existing = await db.RecoveryCodes.Where(c => c.UserId == userId).ToListAsync(cancellationToken);
        db.RecoveryCodes.RemoveRange(existing);
    }
}
