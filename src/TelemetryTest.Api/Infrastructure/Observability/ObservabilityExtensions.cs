using Microsoft.OpenTelemetry;
using OpenTelemetry;
using OpenTelemetry.Instrumentation.AspNetCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace TelemetryTest.Api.Infrastructure.Observability;

/// <summary>
/// Telemetry to Application Insights through Microsoft's OpenTelemetry distro, or none.
/// </summary>
/// <remarks>
/// The production set-up under test, unchanged apart from its comments. The distro sends straight to Application
/// Insights and is never asked for <see cref="ExportTarget.Otlp"/>, so no record reaches Application Insights twice.
/// </remarks>
internal static class ObservabilityExtensions
{
    public static IServiceCollection AddApiObservability(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        TelemetrySettings settings = TelemetrySettings.From(configuration);

        services
            .AddOpenTelemetry()
            .UseMicrosoftOpenTelemetry(options =>
            {
                // Said explicitly rather than left to the distro's detection: with no connection string the answer is
                // None, which builds no pipeline at all, and with one it is Azure Monitor and nothing else.
                options.Exporters = settings.SendsToApplicationInsights ? ExportTarget.AzureMonitor : ExportTarget.None;
                options.AzureMonitor.ConnectionString = settings.ConnectionString;

                // The distro's default keeps five traces a second whatever the ratio says; null selects fixed-percentage
                // sampling, as the Learn sample does. Standard metrics are counted before sampling either way.
                options.AzureMonitor.TracesPerSecond = null;
                options.AzureMonitor.SamplingRatio = (float)settings.SamplingRatio;

                // The suite's telemetry hosts point at a closed local port. Live Metrics would hold a connection open to
                // it, and the offline store would keep every failed batch on the test machine's disk.
                if (environment.IsEnvironment("Testing"))
                {
                    options.AzureMonitor.EnableLiveMetrics = false;
                    options.AzureMonitor.DisableOfflineStorage = true;
                }
            })

            // After the distro, on purpose: its Container Apps detector names the service after the container app, and
            // the resource added last wins. Application Map shows this name instead.
            .ConfigureResource(resource => resource.AddService(settings.ServiceName, autoGenerateServiceInstanceId: false))

            // The distro has no runtime metrics: GC, heap, thread pool.
            .WithMetrics(metrics => metrics.AddRuntimeInstrumentation());

        // The platform's liveness and readiness probes arrive every few seconds and say nothing about the product.
        // http.server.request.duration still counts them; requests and traces stay clean.
        services.Configure<AspNetCoreTraceInstrumentationOptions>(options =>
            options.Filter = context => !context.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase));

        return services;
    }
}
