using System.Globalization;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using Hrms.Worker;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Hrms.Logging;
using Serilog;
using Serilog.Formatting.Compact;

var builder = Host.CreateApplicationBuilder(args);

// WriteTo.Conditional builds the inner sink eagerly (the predicate only filters events),
// so the Seq sink must be added with a plain `if` or a missing URL throws at startup.
var seqServerUrl = builder.Configuration["Seq:ServerUrl"];

builder.Services.AddSerilog((services, logger) =>
{
    logger
        .ReadFrom.Configuration(builder.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        // After FromLogContext so pushed properties are masked too.
        .Enrich.With<SensitiveDataEnricher>()
        .WriteTo.Console(new RenderedCompactJsonFormatter());

    if (!string.IsNullOrWhiteSpace(seqServerUrl))
    {
        logger.WriteTo.Seq(seqServerUrl, formatProvider: CultureInfo.InvariantCulture);
    }
});

var otel = builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("hrms-worker"))
    .WithTracing(t => t.AddHttpClientInstrumentation())
    .WithMetrics(m => m.AddHttpClientInstrumentation().AddRuntimeInstrumentation());
if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
{
    otel.UseOtlpExporter();
}

builder.Services.AddHrmsWorker(builder.Configuration.GetConnectionString("Mysql"));

await builder.Build().RunAsync();
