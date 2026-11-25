using System.Globalization;
using System.Diagnostics;
using Asp.Versioning;
using Asp.Versioning.Builder;
using Hrms.Api;
using Hrms.Api.Health;
using Hrms.Core.Api;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Scalar.AspNetCore;
using Serilog;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, logger) => logger
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console(new RenderedCompactJsonFormatter())
    .WriteTo.Conditional(
        _ => !string.IsNullOrWhiteSpace(context.Configuration["Seq:ServerUrl"]),
        sink => sink.Seq(context.Configuration["Seq:ServerUrl"]!, formatProvider: CultureInfo.InvariantCulture)));

builder.Services.AddHrmsObservability(builder.Configuration, "hrms-api");
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
{
    var problem = context.ProblemDetails;
    problem.Extensions["traceId"] = Activity.Current?.Id ?? context.HttpContext.TraceIdentifier;
    problem.Extensions.TryAdd("code", $"http.{problem.Status}");
});
builder.Services.AddExceptionHandler<ApiExceptionHandler>();

builder.Services.AddApiVersioning(options =>
    {
        options.DefaultApiVersion = new ApiVersion(1, 0);
        options.AssumeDefaultVersionWhenUnspecified = false;
        options.ReportApiVersions = true;
        options.ApiVersionReader = new UrlSegmentApiVersionReader();
    })
    .AddApiExplorer(options =>
    {
        options.GroupNameFormat = "'v'V";
        options.SubstituteApiVersionInUrl = true;
    })
    .AddOpenApi();

builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"])
    .AddDependencyChecks(builder.Configuration);

var app = builder.Build();

// Correlation first so the id is in the log scope for the exception handler and survives on error responses.
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseSerilogRequestLogging();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = check => check.Tags.Contains("live") });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains(DependencyHealthChecks.ReadyTag) });

ApiVersionSet versionSet = app.NewApiVersionSet().HasApiVersion(new ApiVersion(1, 0)).Build();
var v1 = app.MapGroup("/api/v{version:apiVersion}").WithApiVersionSet(versionSet);
v1.MapCoreEndpoints();

// The API description is not served in Production until it can sit behind authorization (P0.4).
if (!app.Environment.IsProduction())
{
    app.MapOpenApi().WithDocumentPerVersion();
    if (app.Environment.IsDevelopment())
    {
        app.MapScalarApiReference();
    }
}

await app.RunAsync();

// Exposed for WebApplicationFactory in integration tests.
public partial class Program;
