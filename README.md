# Microsoft.OpenTelemetry: SamplingRatio is not applied

A self-contained repro. No Azure resource is needed.

**Question.** Does `Microsoft.OpenTelemetry` 1.1.0 apply fixed-percentage sampling when it is configured as
[Microsoft Learn shows](https://learn.microsoft.com/en-us/azure/azure-monitor/app/opentelemetry-configuration?tabs=aspnetcore#configure-sampling-in-code)
(`TracesPerSecond = null`, `SamplingRatio = 0.1`)?

**Answer.** No. Every trace is exported, and every exported item still carries `sampleRate` from the configured ratio,
so Application Insights multiplies it: request counts are inflated by `1 / SamplingRatio` and nothing is saved. The same
options handed to `Azure.Monitor.OpenTelemetry.AspNetCore` work. Setting the exporter's sampler again after the distro
also works.

## How it is measured

The sampling decision is made inside the app; Application Insights only sees what is sent to it. The API opens a fake
ingestion endpoint in its own process, on the port the connection string's `IngestionEndpoint` names. The exporter
posts there exactly what it would post to Azure, and the endpoint counts each item the way Application Insights does.

```
request ─► span ─► SAMPLER ─► exporter ─► POST /v2.1/track ─► fake ingestion (same process, :590x)
                   ▲                                           ▲
                   make probe reads here                       make stats reads here
```

- `make probe` reads the sampler the `TracerProvider` holds after every configuration callback has run, and how many
  incoming request spans it kept (`Recorded`) or let go (`RecordOnly`).
- `make stats` reads what reached the endpoint: rows per telemetry type (`count()` in KQL) and the operations those rows
  stand for, `100 / sampleRate` each (`sum(itemCount)`).

## The set-up under test

`src/TelemetryTest.Api/Infrastructure/Observability/` and the logging lines of `Program.cs` are a production API's
telemetry set-up, unchanged apart from comments:

- `Microsoft.OpenTelemetry` 1.1.0, OpenTelemetry 1.18.0, `Serilog.AspNetCore` 10.0.0, .NET 10.
- `UseMicrosoftOpenTelemetry` with `Exporters = AzureMonitor`, `TracesPerSecond = null` and `SamplingRatio` from
  `OpenTelemetry:SamplingRatio` (0.1 here). Live Metrics and offline storage on, as outside tests.
- Serilog hands every log line to OpenTelemetry (`writeToProviders: true`); `UseSerilogRequestLogging` writes one line
  per request.

Everything under `src/TelemetryTest.Api/Lab/` is the test bench and is added after that set-up without changing it:
the fake ingestion endpoint, the probe, two demo endpoints, a filter that keeps the bench's own requests out of the
traces (it wraps the set-up's `/health` filter), and the variants below. Statsbeat is off in `launchSettings.json` so
the repro makes no outbound calls.

## Three variants, one API

| Profile | Port | What |
|---|---|---|
| `distro` (A) | 5101 | The set-up as it is |
| `control` (B) | 5102 | The same options handed to `UseAzureMonitor` (`Azure.Monitor.OpenTelemetry.AspNetCore` 1.6.0) instead of `UseMicrosoftOpenTelemetry`. Same exporter, 1.9.0 (`Lab/ControlObservability.cs`) |
| `fixed` (C) | 5103 | A, then `SetSampler` with a copy of the exporter's internal `ApplicationInsightsSampler`, registered after the distro (`Lab/AppInsightsCompatibleSampler.cs`, `Lab/LabSetup.cs`) |

`GET /api/orders/{id}` is one trace: the order request, an HTTP call the API makes to itself (a dependency), the stock
request it reaches, and their log lines. A sampler that works keeps or lets go of all of them together.

## Run it

Requires the .NET 10 SDK and four terminal tabs in this folder.

1. `make build`
2. Tab 1: `make distro`, tab 2: `make control`, tab 3: `make fixed`. Each prints
   `fake Application Insights listening on http://localhost:590x/`; every batch received shows up in the same tab.
3. Tab 4, before any traffic: `make probe PORT=5101`, then `5102`, `5103`. The configured ratio is 0.1 in all three;
   the `sampler` field is not.
4. `make load PORT=5101`, then `5102`, `5103`: 1,000 orders (2,000 requests) each, then the probe.
5. Wait 70 seconds (traces are exported every 5 s, standard metrics every 60 s), then `make stats PORT=5101`, `5102`,
   `5103`.
6. `make reset PORT=...` clears an endpoint's counts; the probe is reset by every `make load`.

Another ratio: `OpenTelemetry__SamplingRatio=0.5 make distro`. Before sharing the folder: `make clean`.

## Result

Ratio 0.1, 1,000 orders per variant. The right answer is **2,000 requests**.

| | A distro | B control | C fixed |
|---|---|---|---|
| Sampler the `TracerProvider` holds | `ParentBased{AlwaysOnSampler}` | `ApplicationInsightsSampler{0.1}` | `AppInsightsCompatibleSampler{0.1}` |
| Request spans kept by the sampler | **2000** | 232 | 168 |
| Request items exported (`count()`) | **2000** | 232 | 168 |
| `sampleRate` on every item | 10 | 10 | 10 |
| Requests Application Insights reports (`sum(itemCount)`) | **20000** | 2320 | 1680 |
| Standard metric request count (before sampling) | 2000 | 2000 | 2000 |

- **A** exports everything and every item counts ten times: no saving, request counts ten times too high.
- **B** does what the documentation says: about 10% exported, `sum(itemCount)` close to the real count.
- **C** behaves as B without leaving the distro.
- B and C are not exactly 2,000 because the decision is a hash of the trace id; with 1,000 traces it lands around 100
  (116 and 84 here). Standard metrics are right in every variant; what goes wrong is anything read from the `requests`
  table.

## Why A behaves this way

1. `UseMicrosoftOpenTelemetry` configures Azure Monitor with the ratio, then calls `UseAgent365` whenever tracing is
   on, whatever `Exporters` says
   ([MicrosoftOpenTelemetryBuilderExtensions.cs#L192](https://github.com/microsoft/opentelemetry-distro-dotnet/blob/v1.1.0/src/Microsoft.OpenTelemetry/MicrosoftOpenTelemetryBuilderExtensions.cs#L192)).
2. `UseAgent365` sets the sampler again, and the last sampler set wins
   ([Agent365OpenTelemetryBuilderExtensions.cs#L90](https://github.com/microsoft/opentelemetry-distro-dotnet/blob/v1.1.0/src/Microsoft.OpenTelemetry/Agent365/Runtime/Agent365OpenTelemetryBuilderExtensions.cs#L90)):

   ```csharp
   tracing.SetSampler(new ParentBasedSampler(
       rootSampler: new AlwaysOnSampler(),
       localParentNotSampled: new AlwaysOnSampler(),
       remoteParentNotSampled: new AlwaysOnSampler()));
   ```

3. The exporter writes `sampleRate` from `SamplingRatio`, not from the sampler's decision
   ([AzureMonitorTraceExporter.cs#L48](https://github.com/Azure/azure-sdk-for-net/blob/Azure.Monitor.OpenTelemetry.Exporter_1.10.0/sdk/monitor/Azure.Monitor.OpenTelemetry.Exporter/src/AzureMonitorTraceExporter.cs#L48),
   1.10.0; the same value is observed on the wire with 1.9.0).

Together: everything is sent, and everything counts `1 / ratio` times.

## Side observations

- Log items that follow their trace's sampling decision (B and C) carry no `sampleRate`, so `sum(itemCount)` over
  `traces` does not stand for the log lines that were let go.
- Each order writes seven log items: two request lines, the order's own line and four from `HttpClient`. The logging
  configuration raises `Microsoft` to Warning but not `System.Net.Http`.

## Credits

`Lab/AppInsightsCompatibleSampler.cs` is a copy of `ApplicationInsightsSampler` and `SamplerUtils` from
[Azure.Monitor.OpenTelemetry.Exporter](https://github.com/Azure/azure-sdk-for-net/tree/main/sdk/monitor/Azure.Monitor.OpenTelemetry.Exporter)
(MIT), where the original is internal.
