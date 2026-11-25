using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Hrms.Api;

/// <summary>Turns unhandled exceptions into problem details without leaking internals.</summary>
public sealed partial class ApiExceptionHandler(IProblemDetailsService problemDetails, ILogger<ApiExceptionHandler> logger)
    : IExceptionHandler
{
    /// <summary>Non-standard status (nginx convention) for requests the client abandoned.</summary>
    public const int ClientClosedRequest = 499;

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var method = httpContext.Request.Method;

        // Client went away: not a server fault, nobody is listening for a body.
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            LogClientCancelled(logger, method);
            httpContext.Response.StatusCode = ClientClosedRequest;
            return true;
        }

        // Malformed or oversized request: the client's mistake, report the status Kestrel chose.
        if (exception is BadHttpRequestException badRequest)
        {
            LogBadRequest(logger, badRequest, method);
            httpContext.Response.StatusCode = badRequest.StatusCode;
            return await problemDetails.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = httpContext,
                ProblemDetails = new ProblemDetails
                {
                    Status = badRequest.StatusCode,
                    Title = "The request could not be processed.",
                    Extensions = { ["code"] = $"http.{badRequest.StatusCode}" },
                },
            });
        }

        LogUnhandled(logger, exception, method, httpContext.Request.Path.Value);

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "An unexpected error occurred.",
                Extensions = { ["code"] = "server.unexpected" },
            },
        });
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Request {Method} was cancelled by the client")]
    private static partial void LogClientCancelled(ILogger logger, string method);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Bad request for {Method}")]
    private static partial void LogBadRequest(ILogger logger, Exception exception, string method);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception for {Method} {Path}")]
    private static partial void LogUnhandled(ILogger logger, Exception exception, string method, string? path);
}
