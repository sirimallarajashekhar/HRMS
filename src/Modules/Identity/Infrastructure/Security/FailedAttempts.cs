using Hrms.Identity.Domain.Security;
using Hrms.Identity.Domain.Users;
using Hrms.Identity.Infrastructure.Persistence;
using Hrms.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Hrms.Identity.Infrastructure.Security;

/// <summary>
/// Counts a wrong password or code against an account and logs it. Parallel guesses update the same account row, so a save that
/// loses that race is reloaded and tried again: losing a count would help the attacker.
/// </summary>
internal static class FailedAttempts
{
    /// <summary>Generous and jittered: a burst of N simultaneous guesses needs up to N attempts for the slowest.</summary>
    private const int Retries = 30;

    /// <summary>
    /// Clears the change tracker whenever it has to retry, so call it with no other change pending.
    /// </summary>
    /// <returns>What counting did, or null when every try lost the race and nothing was recorded.</returns>
    public static async Task<LockoutOutcome?> RecordAsync(
        IdentityDbContext db,
        Guid userId,
        DateTime now,
        LockoutPolicy policy,
        string during,
        string factor,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < Retries; attempt++)
        {
            try
            {
                var user = await db.Users.SingleAsync(u => u.Id == userId, cancellationToken);
                var outcome = user.RegisterFailedAttempt(now, policy);
                SecurityEvents.Refused(db, user, outcome, during, factor);
                await db.SaveChangesAsync(cancellationToken);
                return outcome;
            }
            catch (Exception ex) when (WriteConflicts.Is(ex))
            {
                db.ChangeTracker.Clear();

                // The random pause spreads the retries out so contenders stop colliding on the same instant.
                await Task.Delay(Random.Shared.Next(1, 4 + (attempt * 2)), cancellationToken);
            }
        }

        return null;
    }
}
