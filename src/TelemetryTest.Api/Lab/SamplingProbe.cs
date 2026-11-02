using System.Diagnostics;
using System.Reflection;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace TelemetryTest.Api.Lab;

/// <summary>
/// Two measurements from inside the app: how many incoming request spans ended, and how many of them the sampler kept.
/// </summary>
/// <remarks>
/// A span the sampler keeps is <c>Recorded</c>; one it lets go is still built (<c>RecordOnly</c>), so the standard
/// metrics count it, and it reaches this processor too. The ratio of the two numbers is the sampler's decision and
/// nothing else: the exporter and the network play no part in it.
/// </remarks>
internal sealed class SamplingProbe : BaseProcessor<Activity>
{
    private long serverSpans;
    private long recorded;

    public long ServerSpans => Interlocked.Read(ref serverSpans);

    public long Recorded => Interlocked.Read(ref recorded);

    public override void OnEnd(Activity data)
    {
        if (data.Kind != ActivityKind.Server)
        {
            return;
        }

        Interlocked.Increment(ref serverSpans);

        if (data.Recorded)
        {
            Interlocked.Increment(ref recorded);
        }
    }

    public void Reset()
    {
        Interlocked.Exchange(ref serverSpans, 0);
        Interlocked.Exchange(ref recorded, 0);
    }

    /// <summary>
    /// The sampler the provider actually holds, after every configuration callback has run.
    /// </summary>
    /// <remarks>
    /// Read by reflection because the SDK keeps it internal. This is the direct evidence for "the last sampler set wins":
    /// the ratio was handed to the exporter's sampler, and this says which sampler is left.
    /// </remarks>
    public static string DescribeSampler(TracerProvider provider)
    {
        PropertyInfo? property = provider.GetType().GetProperty(
            "Sampler",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        return property?.GetValue(provider) is Sampler sampler ? sampler.Description : "unknown";
    }
}
