using Azure.Monitor.OpenTelemetry.AspNetCore;
using OpenTelemetry;
using OpenTelemetry.Instrumentation.AspNetCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using TelemetryTest.Api.Infrastructure.Observability;

namespace TelemetryTest.Api.Lab;

/// <summary>
/// Variant B, the control: <see cref="ObservabilityExtensions.AddApiObservability"/> line for line, with
/// <c>UseAzureMonitor</c> in place of <c>UseMicrosoftOpenTelemetry</c>. Every setting is the same one, set the same way.
/// </summary>
/// <remarks>
/// The distro's <c>Exporters</c> switch has no counterpart here: this package is simply not called without a
/// connection string, and the lab always has one.
/// </remarks>
internal static class ControlObservability
{
    public static IServiceCollection AddControlObservability(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        TelemetrySettings settings = TelemetrySettings.From(configuration);

        services
            .AddOpenTelemetry()
            .UseAzureMonitor(options =>
            {
                options.ConnectionString = settings.ConnectionString;

                options.TracesPerSecond = null;
                options.SamplingRatio = (float)settings.SamplingRatio;

                if (environment.IsEnvironment("Testing"))
                {
                    options.EnableLiveMetrics = false;
                    options.DisableOfflineStorage = true;
                }
            })
            .ConfigureResource(resource => resource.AddService(settings.ServiceName, autoGenerateServiceInstanceId: false))
            .WithMetrics(metrics => metrics.AddRuntimeInstrumentation());

        services.Configure<AspNetCoreTraceInstrumentationOptions>(options =>
            options.Filter = context => !context.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase));

        return services;
    }
}
