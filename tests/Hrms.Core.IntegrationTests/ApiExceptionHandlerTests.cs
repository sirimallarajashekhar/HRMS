using FluentAssertions;
using Hrms.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hrms.Core.IntegrationTests;

public class ApiExceptionHandlerTests
{
    [Fact]
    public async Task TryHandle_WhenClientCancelled_Returns499WithoutWritingProblem()
    {
        using var cts = new CancellationTokenSource();
        var context = new DefaultHttpContext { RequestAborted = cts.Token };
        await cts.CancelAsync();
        var problems = new CapturingProblemDetailsService();
        var handler = new ApiExceptionHandler(problems, NullLogger<ApiExceptionHandler>.Instance);

        var handled = await handler.TryHandleAsync(context, new OperationCanceledException(), CancellationToken.None);

        handled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(ApiExceptionHandler.ClientClosedRequest);
        problems.Written.Should().BeEmpty();
    }

    [Fact]
    public async Task TryHandle_WhenOperationCancelledButClientStillConnected_Returns500()
    {
        var context = new DefaultHttpContext();
        var problems = new CapturingProblemDetailsService();
        var handler = new ApiExceptionHandler(problems, NullLogger<ApiExceptionHandler>.Instance);

        await handler.TryHandleAsync(context, new OperationCanceledException(), CancellationToken.None);

        context.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        problems.Written.Should().ContainSingle();
    }

    [Theory]
    [InlineData(StatusCodes.Status400BadRequest)]
    [InlineData(StatusCodes.Status413PayloadTooLarge)]
    public async Task TryHandle_WhenBadHttpRequest_ReportsKestrelStatusWithStableCode(int status)
    {
        var context = new DefaultHttpContext();
        var problems = new CapturingProblemDetailsService();
        var handler = new ApiExceptionHandler(problems, NullLogger<ApiExceptionHandler>.Instance);

        await handler.TryHandleAsync(context, new BadHttpRequestException("boom", status), CancellationToken.None);

        context.Response.StatusCode.Should().Be(status);
        var problem = problems.Written.Should().ContainSingle().Subject.ProblemDetails;
        problem.Status.Should().Be(status);
        problem.Extensions["code"].Should().Be($"http.{status}");
        problem.Detail.Should().BeNull();
    }

    private sealed class CapturingProblemDetailsService : IProblemDetailsService
    {
        public List<ProblemDetailsContext> Written { get; } = [];

        public ValueTask WriteAsync(ProblemDetailsContext context)
        {
            Written.Add(context);
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> TryWriteAsync(ProblemDetailsContext context)
        {
            Written.Add(context);
            return ValueTask.FromResult(true);
        }
    }
}
