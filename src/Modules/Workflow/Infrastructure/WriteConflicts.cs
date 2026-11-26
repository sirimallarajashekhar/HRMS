using Microsoft.EntityFrameworkCore;
using MySqlConnector;

namespace Hrms.Workflow.Infrastructure;

/// <summary>
/// Recognises a save that lost a race, in all the shapes it takes. Besides a stale row version, MySQL can resolve two decisions on
/// the same workflow as a deadlock (each holds a shared lock on the workflow row through a new history row, and both then want to
/// update it) and roll one back. Pomelo reports that wrapped in an InvalidOperationException, so the whole chain is examined. In every
/// case nothing was written and the caller may simply try again. Any other database error is not a conflict and must surface.
/// </summary>
internal static class WriteConflicts
{
    public static bool Is(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is DbUpdateConcurrencyException
                || current is MySqlException
                {
                    ErrorCode: MySqlErrorCode.LockDeadlock or MySqlErrorCode.LockWaitTimeout or MySqlErrorCode.DuplicateKeyEntry,
                })
            {
                return true;
            }
        }

        return false;
    }
}
