using FluentAssertions;
using Hrms.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Hrms.Core.UnitTests;

public class WriteConflictsTests
{
    [Fact]
    public void Is_LooksThroughTheWholeExceptionChain() =>
        WriteConflicts.Is(new InvalidOperationException("wrapped", new DbUpdateConcurrencyException())).Should().BeTrue();

    [Fact]
    public void ASaveThatFailedForAnotherReason_IsADatabaseFailureButNotAConflict()
    {
        var failure = new InvalidOperationException("wrapped", new DbUpdateException());

        WriteConflicts.IsDatabaseFailure(failure).Should().BeTrue();
        WriteConflicts.Is(failure).Should().BeFalse();
    }

    [Fact]
    public void IsDatabaseFailure_LeavesCancellationAndFaultsInTheCodeAlone()
    {
        WriteConflicts.IsDatabaseFailure(new OperationCanceledException("cancelled", new DbUpdateException())).Should().BeFalse();
        WriteConflicts.IsDatabaseFailure(new InvalidOperationException("a bug")).Should().BeFalse();
    }
}
