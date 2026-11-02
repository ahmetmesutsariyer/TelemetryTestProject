using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace TelemetryTest.Api.Lab;

/// <summary>
/// Counts what reaches the fake ingestion endpoint the way Application Insights would: one row per item, and an item
/// stands for <c>100 / sampleRate</c> operations. The second number is <c>sum(itemCount)</c> in KQL.
/// </summary>
internal sealed class IngestionLedger
{
    private const string RequestsDurationMetric = "requests/duration";

    private readonly ConcurrentDictionary<string, Tally> byKind = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Tally> requestsByName = new(StringComparer.Ordinal);
    private double standardRequestCount;
    private long liveMetricsPings;

    public int Take(string payload)
    {
        int received = 0;
        SortedDictionary<string, int> batch = new(StringComparer.Ordinal);

        foreach (string line in payload.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement item = document.RootElement;

            // An item without sampleRate is unsampled (100%), which is how the exporter writes a ratio of 1.
            double sampleRate = item.TryGetProperty("sampleRate", out JsonElement rate) ? rate.GetDouble() : 100d;
            double represents = 100d / sampleRate;

            JsonElement data = item.GetProperty("data");
            string kind = data.GetProperty("baseType").GetString()!.Replace("Data", "", StringComparison.Ordinal);
            JsonElement baseData = data.GetProperty("baseData");

            byKind.GetOrAdd(kind, _ => new Tally()).Add(represents, sampleRate);

            if (kind == "Request" && baseData.TryGetProperty("name", out JsonElement name))
            {
                requestsByName.GetOrAdd(name.GetString() ?? "?", _ => new Tally()).Add(represents, sampleRate);
            }

            if (kind == "Metric")
            {
                CountStandardRequests(baseData);
            }

            batch[kind] = batch.GetValueOrDefault(kind) + 1;
            received++;
        }

        Console.WriteLine(
            $"  <- ingestion  {received,5} items   " + string.Join("  ", batch.Select(pair => $"{pair.Key} {pair.Value}")));

        return received;
    }

    public void Ping() => Interlocked.Increment(ref liveMetricsPings);

    public void Clear()
    {
        byKind.Clear();
        requestsByName.Clear();
        Interlocked.Exchange(ref standardRequestCount, 0);
        Console.WriteLine("  -- ingestion ledger cleared");
    }

    public string Report()
    {
        CultureInfo culture = CultureInfo.InvariantCulture;
        StringBuilder report = new();

        report.AppendLine("Telemetry received, as Application Insights would store it");
        report.AppendLine("  items       = rows in the table             (KQL: count())");
        report.AppendLine("  represented = operations the rows stand for (KQL: sum(itemCount))");
        report.AppendLine();
        report.AppendLine(culture, $"{"kind",-18} {"items",8} {"represented",12}  sampleRate");

        foreach ((string kind, Tally tally) in byKind.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            report.AppendLine(culture, $"{kind,-18} {tally.Items,8} {tally.Represented,12:0}  {tally.Rates}");
        }

        report.AppendLine();
        report.AppendLine("Requests by operation");

        foreach ((string name, Tally tally) in requestsByName.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            report.AppendLine(culture, $"{name,-30} {tally.Items,8} {tally.Represented,12:0}");
        }

        report.AppendLine();
        report.AppendLine(culture, $"Standard metric requests/duration (counted before sampling): {Volatile.Read(ref standardRequestCount):0}");
        report.AppendLine(culture, $"Live Metrics pings answered: {Interlocked.Read(ref liveMetricsPings)}");

        return report.ToString();
    }

    private void CountStandardRequests(JsonElement baseData)
    {
        if (!baseData.TryGetProperty("properties", out JsonElement properties)
            || !properties.TryGetProperty("_MS.MetricId", out JsonElement metricId)
            || metricId.GetString() != RequestsDurationMetric)
        {
            return;
        }

        foreach (JsonElement metric in baseData.GetProperty("metrics").EnumerateArray())
        {
            double count = metric.TryGetProperty("count", out JsonElement value) ? value.GetDouble() : 1d;
            double seen;

            do
            {
                seen = Volatile.Read(ref standardRequestCount);
            }
            while (Interlocked.CompareExchange(ref standardRequestCount, seen + count, seen) != seen);
        }
    }

    private sealed class Tally
    {
        private readonly Lock gate = new();
        private readonly SortedSet<double> rates = [];

        public long Items { get; private set; }

        public double Represented { get; private set; }

        public string Rates
        {
            get
            {
                lock (gate)
                {
                    return string.Join(",", rates.Select(rate => rate.ToString(CultureInfo.InvariantCulture)));
                }
            }
        }

        public void Add(double represents, double sampleRate)
        {
            lock (gate)
            {
                Items++;
                Represented += represents;
                rates.Add(sampleRate);
            }
        }
    }
}
