using System.Globalization;
using System.Diagnostics;
using Asp.Versioning;
using Asp.Versioning.Builder;
using Hrms.Api;
using Hrms.Api.Health;
using Hrms.Core.Api;
using Hrms.Core.Infrastructure;
using Hrms.Persistence;
using Hrms.Http.Tenancy;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Scalar.AspNetCore;
using Hrms.Logging;
using Serilog;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, logger) =>
{
    logger
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        // After FromLogContext so pushed properties are masked too.
        .Enrich.With<SensitiveDataEnricher>()
        .WriteTo.Console(new RenderedCompactJsonFormatter());

    // WriteTo.Conditional builds the inner sink eagerly (the predicate only filters events),
    // so the Seq sink must be added with a plain `if` or a missing URL throws at startup.
    var seqServerUrl = context.Configuration["Seq:ServerUrl"];
    if (!string.IsNullOrWhiteSpace(seqServerUrl))
    {
        logger.WriteTo.Seq(seqServerUrl, formatProvider: CultureInfo.InvariantCulture);
    }
});

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

var mysqlConnectionString = builder.Configuration.GetConnectionString("Mysql");
builder.Services.AddHrmsTenancy();
builder.Services.AddHrmsPersistence(mysqlConnectionString);
// After AddHrmsTenancy so the real tenant directory replaces its deny-all default.
builder.Services.AddCoreInfrastructure(mysqlConnectionString);

builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"])
    .AddDependencyChecks(builder.Configuration, builder.Environment);

var app = builder.Build();

// Correlation first so the id is in the log scope for the exception handler and survives on error responses.
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseSerilogRequestLogging();

// Routing first so the tenant middleware can read endpoint metadata; the tenant is resolved before any endpoint runs.
app.UseRouting();
app.UseHrmsTenancy();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = check => check.Tags.Contains("live") }).AllowWithoutTenant();
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains(DependencyHealthChecks.ReadyTag) }).AllowWithoutTenant();

ApiVersionSet versionSet = app.NewApiVersionSet().HasApiVersion(new ApiVersion(1, 0)).Build();
var v1 = app.MapGroup("/api/v{version:apiVersion}").WithApiVersionSet(versionSet);
v1.MapCoreEndpoints();

// The API description is not served in Production until it can sit behind authorization (P0.4).
if (!app.Environment.IsProduction())
{
    app.MapOpenApi().AllowWithoutTenant().WithDocumentPerVersion();
    if (app.Environment.IsDevelopment())
    {
        app.MapScalarApiReference().AllowWithoutTenant();
    }
}

await app.RunAsync();

// Exposed for WebApplicationFactory in integration tests.
public partial class Program;
