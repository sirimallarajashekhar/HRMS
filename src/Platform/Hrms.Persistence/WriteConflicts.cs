using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;

namespace Hrms.Persistence;

/// <summary>
/// Recognises a save that lost a race, in all the shapes it takes. Besides a stale row version, MySQL can resolve two changes to the
/// same rows as a deadlock (each holds a shared lock on a parent row through a new child row, and both then want to update it) and
/// roll one back. Pomelo reports that wrapped in an InvalidOperationException, so the whole chain is examined. In every case nothing
/// was written and the caller may simply try again. Any other database error is not a conflict.
/// </summary>
public static class WriteConflicts
{
    public static bool Is(Exception exception) => Chain(exception).Any(current =>
        current is DbUpdateConcurrencyException
            or MySqlException
            {
                ErrorCode: MySqlErrorCode.LockDeadlock or MySqlErrorCode.LockWaitTimeout or MySqlErrorCode.DuplicateKeyEntry,
            });

    /// <summary>
    /// Whether the database, or saving to it, is what failed, conflicts included. A cancelled request is not a failure, and neither
    /// is a fault in this code (such as a refused cross-tenant write): those must surface.
    /// </summary>
    public static bool IsDatabaseFailure(Exception exception) =>
        exception is not OperationCanceledException
        && Chain(exception).Any(current => current is DbException or DbUpdateException);

    private static IEnumerable<Exception> Chain(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            yield return current;
        }
    }
}
