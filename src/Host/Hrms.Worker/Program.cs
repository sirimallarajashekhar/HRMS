using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using Hrms.Worker;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Formatting.Compact;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSerilog((services, logger) => logger
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console(new RenderedCompactJsonFormatter()));

var otel = builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("hrms-worker"))
    .WithTracing(t => t.AddHttpClientInstrumentation())
    .WithMetrics(m => m.AddHttpClientInstrumentation().AddRuntimeInstrumentation());
if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
{
    otel.UseOtlpExporter();
}

// Hangfire and queue consumers are registered here from P0.2 / P1.3 onwards.
builder.Services.AddHostedService<HeartbeatService>();

await builder.Build().RunAsync();
