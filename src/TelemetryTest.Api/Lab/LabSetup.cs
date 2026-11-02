using Microsoft.AspNetCore.Http.HttpResults;
using OpenTelemetry.Instrumentation.AspNetCore;
using OpenTelemetry.Trace;
using TelemetryTest.Api.Infrastructure.Observability;

namespace TelemetryTest.Api.Lab;

/// <summary>
/// The test bench: the fake ingestion endpoint, the probe, the demo endpoints and variant C. Added after the telemetry
/// set-up and without changing it.
/// </summary>
internal static partial class LabSetup
{
    private const string SelfClient = "self";

    public static WebApplicationBuilder AddLab(this WebApplicationBuilder builder, LabVariant variant)
    {
        TelemetrySettings telemetry = TelemetrySettings.From(builder.Configuration);
        SamplingProbe probe = new();
        IngestionLedger ledger = new();

        builder.Services.AddSingleton(probe);
        builder.Services.AddSingleton(ledger);
        builder.Services.AddSingleton(new LabState(variant, telemetry));
        builder.Services.AddHostedService(_ => new FakeIngestion(ledger, IngestionEndpointOf(telemetry)));

        // The order calls the API itself, so the order and its stock read land in one trace. The address is whatever
        // the launch profile bound; writing the port a second time would let the two drift.
        Uri self = new((builder.Configuration["urls"] ?? throw new InvalidOperationException("The app has no bound address.")).Split(';')[0]);
        builder.Services.AddHttpClient(SelfClient, client => client.BaseAddress = self);

        // Registered after the telemetry set-up, so it runs after the distro's own configuration, Agent365's included.
        builder.Services.ConfigureOpenTelemetryTracerProvider(tracing =>
        {
            tracing.AddProcessor(probe);

            // Variant C, the candidate fix: the last sampler set wins, the same way Agent365's sampler wins over the
            // ratio in variant A.
            if (variant == LabVariant.Fixed)
            {
                tracing.SetSampler(new AppInsightsCompatibleSampler((float)telemetry.SamplingRatio));
            }
        });

        // Wraps the /health filter rather than replacing it. The lab's own calls are not the traffic being measured,
        // and the fake ingestion's requests would otherwise be traced and exported to it in turn.
        builder.Services.PostConfigure<AspNetCoreTraceInstrumentationOptions>(options =>
        {
            Func<HttpContext, bool>? setUpFilter = options.Filter;
            options.Filter = context =>
                !IsLabPath(context.Request.Path) && (setUpFilter?.Invoke(context) ?? true);
        });

        return builder;
    }

    public static WebApplication MapLabEndpoints(this WebApplication app)
    {
        // One call is one trace: the order request, the outgoing dependency, the stock request it reaches, and their log
        // lines. A sampler that works keeps or lets go of all of them together.
        app.MapGet("/api/orders/{id:int}", ReadOrderAsync);
        app.MapGet("/api/stock/{id:int}", (int id) => TypedResults.Ok(id % 7));

        app.MapGet("/lab/probe", (SamplingProbe probe, TracerProvider tracer, LabState state) =>
            TypedResults.Ok(new ProbeAnswer(
                state.Variant.ToString(),
                state.Telemetry.SamplingRatio,
                SamplingProbe.DescribeSampler(tracer),
                probe.ServerSpans,
                probe.Recorded,
                probe.ServerSpans - probe.Recorded)));

        app.MapPost("/lab/probe/reset", (SamplingProbe probe) =>
        {
            probe.Reset();
            return TypedResults.NoContent();
        });

        app.MapGet("/lab/stats", (IngestionLedger ledger) => TypedResults.Text(ledger.Report()));

        app.MapDelete("/lab/stats", (IngestionLedger ledger) =>
        {
            ledger.Clear();
            return TypedResults.NoContent();
        });

        return app;
    }

    private static bool IsLabPath(PathString path) =>
        path.StartsWithSegments("/lab", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments(FakeIngestion.TrackPath, StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments(FakeIngestion.LiveMetricsPath, StringComparison.OrdinalIgnoreCase);

    /// <summary>The fake ingestion listens where the connection string sends the telemetry: one source for the port.</summary>
    private static Uri IngestionEndpointOf(TelemetrySettings telemetry)
    {
        const string Key = "IngestionEndpoint=";

        string? part = telemetry.ConnectionString?
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(part => part.StartsWith(Key, StringComparison.OrdinalIgnoreCase));

        Uri endpoint = part is null
            ? throw new InvalidOperationException("The connection string names no IngestionEndpoint.")
            : new Uri(part[Key.Length..]);

        return endpoint.IsLoopback
            ? endpoint
            : throw new InvalidOperationException($"The lab only sends to itself; IngestionEndpoint is {endpoint}.");
    }

    private static async Task<Ok<OrderAnswer>> ReadOrderAsync(
        int id,
        IHttpClientFactory clients,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        HttpClient client = clients.CreateClient(SelfClient);
        string stock = await client.GetStringAsync(new Uri($"/api/stock/{id}", UriKind.Relative), cancellationToken);

        ILogger logger = loggers.CreateLogger("TelemetryTest.Orders");
        OrderRead(logger, id, stock);

        return TypedResults.Ok(new OrderAnswer(id, stock));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Order {OrderId} read with stock {Stock}")]
    private static partial void OrderRead(ILogger logger, int orderId, string stock);

    private sealed record LabState(LabVariant Variant, TelemetrySettings Telemetry);

    private sealed record OrderAnswer(int Id, string Stock);

    /// <summary>
    /// <c>Recorded</c>: what the sampler kept for export. <c>RecordOnly</c>: built, counted by metrics, not exported.
    /// </summary>
    private sealed record ProbeAnswer(
        string Variant,
        double ConfiguredRatio,
        string Sampler,
        long ServerSpans,
        long Recorded,
        long RecordOnly);
}
