using System.IO.Compression;

namespace TelemetryTest.Api.Lab;

/// <summary>
/// Stands in for Application Insights: a second, bare web host in the same process, on the port the connection
/// string's IngestionEndpoint names. The exporter posts here exactly what it would post to Azure.
/// </summary>
/// <remarks>
/// <para>
/// A host of its own rather than routes on the API: the API's request logging would write a line for every batch it
/// receives, that line would be exported in the next batch, and the lab would be measuring itself. This host has no
/// Serilog and no logging providers.
/// </para>
/// <para>
/// ASP.NET Core's activity source is process-wide, so its requests still reach the API's tracer; <see cref="LabSetup"/>
/// filters them out by path.
/// </para>
/// </remarks>
internal sealed class FakeIngestion(IngestionLedger ledger, Uri endpoint) : IHostedService, IAsyncDisposable
{
    public const string TrackPath = "/v2.1/track";
    public const string LiveMetricsPath = "/QuickPulseService.svc";

    private WebApplication? host;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        WebApplicationBuilder builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseKestrelCore().ConfigureKestrel(kestrel => kestrel.ListenLocalhost(endpoint.Port));
        builder.Services.AddRoutingCore();

        host = builder.Build();

        // The body is newline-delimited JSON, one telemetry item per line. Answering with the track response keeps the
        // exporter from retrying.
        host.MapPost(TrackPath, async (HttpRequest request) =>
        {
            Stream body = request.Headers.ContentEncoding.ToString().Contains("gzip", StringComparison.OrdinalIgnoreCase)
                ? new GZipStream(request.Body, CompressionMode.Decompress)
                : request.Body;

            using StreamReader reader = new(body);
            int received = ledger.Take(await reader.ReadToEndAsync(request.HttpContext.RequestAborted));

            return Results.Json(new TrackResponse(received, received, []));
        });

        // Live Metrics is on outside Testing, so it is on here: the exporter pings LiveEndpoint every few seconds. The
        // answer is the one Application Insights gives while nobody has Live Metrics open in the portal: not subscribed,
        // keep pinging, post nothing. Traces take the other road.
        host.MapPost($"{LiveMetricsPath}/ping", (HttpResponse response) =>
        {
            ledger.Ping();
            response.Headers["x-ms-qps-subscribed"] = "false";
            return Results.Ok();
        });

        // A request nothing here expects means a setting did not take; it is shown, not answered quietly.
        host.MapFallback((HttpRequest request) =>
        {
            Console.WriteLine($"  !! ingestion did not expect {request.Method} {request.Path}");
            return Results.NotFound();
        });

        await host.StartAsync(cancellationToken);
        Console.WriteLine($"  fake Application Insights listening on {endpoint}");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (host is not null)
        {
            await host.StopAsync(cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (host is not null)
        {
            await host.DisposeAsync();
        }
    }

    private sealed record TrackResponse(int ItemsReceived, int ItemsAccepted, object[] Errors);
}
