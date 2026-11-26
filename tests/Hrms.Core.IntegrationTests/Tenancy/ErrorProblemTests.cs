using FluentAssertions;
using Hrms.Http;
using Hrms.SharedKernel;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Hrms.Core.IntegrationTests.Tenancy;

public class ErrorProblemTests
{
    [Theory]
    [InlineData(ErrorType.Validation, 422)]
    [InlineData(ErrorType.NotFound, 404)]
    [InlineData(ErrorType.Conflict, 409)]
    [InlineData(ErrorType.Forbidden, 403)]
    [InlineData(ErrorType.Unexpected, 500)]
    public void ToProblem_MapsErrorTypeToStatusAndKeepsTheStableCode(ErrorType type, int status)
    {
        var problem = (ProblemHttpResult)new Error("leave.balance.insufficient", "Not enough leave.", type).ToProblem();

        problem.StatusCode.Should().Be(status);
        problem.ProblemDetails.Extensions["code"].Should().Be("leave.balance.insufficient");
    }

    [Fact]
    public void ToProblem_ForBusinessFailure_ExplainsTheFailure()
    {
        var problem = (ProblemHttpResult)Error.Conflict("employee.code.duplicate", "That employee code is taken.").ToProblem();

        problem.ProblemDetails.Title.Should().Be("That employee code is taken.");
        problem.ProblemDetails.Detail.Should().Be("That employee code is taken.");
    }

    [Fact]
    public void ToProblem_ForUnexpectedFailure_DoesNotExposeTheMessage()
    {
        var problem = (ProblemHttpResult)new Error("db.exploded", "Connection string Server=prod;Password=hunter2 failed").ToProblem();

        problem.ProblemDetails.Detail.Should().BeNull();
        problem.ProblemDetails.Title.Should().NotContain("hunter2").And.NotContain("Server=");
    }

    [Fact]
    public void ToHttpResult_OnSuccess_RunsTheSuccessMapping()
    {
        var result = Result.Success(7).ToHttpResult(value => TypedResults.Ok(value));

        result.Should().BeOfType<Ok<int>>().Which.Value.Should().Be(7);
    }

    [Fact]
    public void ToHttpResult_OnFailure_ReturnsTheProblemAndSkipsTheSuccessMapping()
    {
        var called = false;
        var result = Result.Failure<int>(Error.NotFound("x.missing", "Nothing here.")).ToHttpResult(value =>
        {
            called = true;
            return TypedResults.Ok(value);
        });

        called.Should().BeFalse();
        result.Should().BeOfType<ProblemHttpResult>().Which.StatusCode.Should().Be(404);
    }

    [Theory]
    [InlineData(ErrorType.Validation)]
    [InlineData(ErrorType.NotFound)]
    [InlineData(ErrorType.Conflict)]
    [InlineData(ErrorType.Forbidden)]
    [InlineData(ErrorType.Unexpected)]
    public void ToStatusCode_CoversEveryErrorType(ErrorType type) =>
        type.ToStatusCode().Should().BeInRange(400, 599);
}
