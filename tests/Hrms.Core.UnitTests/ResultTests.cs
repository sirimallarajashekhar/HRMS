using FluentAssertions;
using Hrms.SharedKernel;

namespace Hrms.Core.UnitTests;

public class ResultTests
{
    private static readonly Error SomeError = new("leave.balance.insufficient", "Not enough balance.");

    [Fact]
    public void Success_WithValue_ExposesValueAndNoError()
    {
        var result = Result.Success(42);

        result.IsSuccess.Should().BeTrue();
        result.IsFailure.Should().BeFalse();
        result.Value.Should().Be(42);
        result.Error.Should().Be(Error.None);
    }

    [Fact]
    public void Failure_WithError_ExposesErrorAndRejectsValueAccess()
    {
        var result = Result.Failure<int>(SomeError);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(SomeError);
        result.Invoking(r => r.Value).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Failure_WithNoneError_Throws()
    {
        var act = () => Result.Failure(Error.None);

        act.Should().Throw<ArgumentException>();
    }
}
