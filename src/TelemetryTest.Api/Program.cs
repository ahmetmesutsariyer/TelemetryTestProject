using System.Diagnostics;
using Serilog;
using Serilog.Events;
using TelemetryTest.Api.Infrastructure.Observability;
using TelemetryTest.Api.Lab;

// The telemetry and logging lines of the production API's Program.cs, in their order. Everything under Lab/ is the test
// bench and comes after that set-up without changing it.

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Serilog also hands every event to the host's logging providers, and OpenTelemetry's is one of them: that is how
// a log line reaches Application Insights under the trace of the request that wrote it. The hand-over is synchronous,
// which is what keeps the trace id on the line. The framework's own Console, Debug and EventSource providers are
// cleared first, or each line would be written twice. Which levels go anywhere is still Serilog's MinimumLevel; the
// Logging:LogLevel filters do not apply on this road.
builder.Logging.ClearProviders();
builder.Host.UseSerilog(
    (context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext(),
    preserveStaticLogger: true,
    writeToProviders: true);

LabVariant variant = LabVariants.From(builder.Configuration);

if (variant == LabVariant.Control)
{
    builder.Services.AddControlObservability(builder.Configuration, builder.Environment);
}
else
{
    builder.Services.AddApiObservability(builder.Configuration, builder.Environment);
}

builder.AddLab(variant);

WebApplication app = builder.Build();

// Written through this host's logger, for the reason given at UseSerilog above.
app.UseSerilogRequestLogging(options =>
{
    options.Logger = app.Services.GetRequiredService<Serilog.ILogger>();
    options.EnrichDiagnosticContext = (diagnostics, context) =>
        diagnostics.Set("TraceId", Activity.Current?.Id ?? context.TraceIdentifier);

    // Health probes and media files that went well are not written: they filled Application Insights with lines nobody
    // reads. What failed is written as before.
    options.GetLevel = (context, _, exception) =>
        exception is not null || context.Response.StatusCode >= StatusCodes.Status500InternalServerError
            ? LogEventLevel.Error
            : context.Response.StatusCode < StatusCodes.Status400BadRequest
                && (context.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase)
                    || context.Request.Path.StartsWithSegments("/media", StringComparison.OrdinalIgnoreCase))
                ? LogEventLevel.Verbose
                : LogEventLevel.Information;
});

app.MapLabEndpoints();

await app.RunAsync();
