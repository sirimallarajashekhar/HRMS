using System.Diagnostics;
using System.Text.RegularExpressions;
using Serilog.Context;

namespace Hrms.Api;

/// <summary>Reads or creates X-Correlation-Id, echoes it on the response and adds it to the log scope.</summary>
public sealed partial class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers[HeaderName].FirstOrDefault();
        if (correlationId is null || !ValidId().IsMatch(correlationId))
        {
            correlationId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        }

        // OnStarting so the header survives the exception handler clearing the response.
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await next(context);
        }
    }

    // Only safe characters are accepted: the value is echoed in a header and written to logs.
    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex ValidId();
}
