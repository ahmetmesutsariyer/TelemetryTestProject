using OpenTelemetry.Trace;

namespace TelemetryTest.Api.Lab;

/// <summary>
/// A copy of the Azure Monitor exporter's <c>ApplicationInsightsSampler</c>: variant C, the candidate fix.
/// </summary>
/// <remarks>
/// <para>
/// Copied from Azure.Monitor.OpenTelemetry.Exporter 1.10.0 (MIT), <c>Internals/ApplicationInsightsSampler.cs</c> and
/// <c>Internals/SamplerUtils.cs</c>; the original is internal. Same decision, same hash: the DJB2 score of the upper-case
/// hex trace id against the ratio. Every service that samples this way keeps or lets go of the same traces.
/// </para>
/// <para>
/// A trace it lets go of is <c>RecordOnly</c>, not <c>Drop</c>: the span is still built, so the standard metrics still
/// count it. OpenTelemetry's own <c>TraceIdRatioBasedSampler</c> drops, and the request counts would fall with the ratio.
/// </para>
/// </remarks>
internal sealed class AppInsightsCompatibleSampler : Sampler
{
    private static readonly SamplingResult RecordOnly = new(SamplingDecision.RecordOnly);
    private static readonly SamplingResult RecordAndSample = new(SamplingDecision.RecordAndSample);

    private readonly float samplingRatio;

    public AppInsightsCompatibleSampler(float samplingRatio)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(samplingRatio, 0f);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(samplingRatio, 1f);

        this.samplingRatio = samplingRatio;
        Description = $"AppInsightsCompatibleSampler{{{samplingRatio}}}";
    }

    public override SamplingResult ShouldSample(in SamplingParameters samplingParameters)
    {
        if (samplingRatio == 1f)
        {
            return RecordAndSample;
        }

        if (samplingRatio == 0f)
        {
            return RecordOnly;
        }

        return Djb2Score(samplingParameters.TraceId.ToHexString().ToUpperInvariant()) < samplingRatio
            ? RecordAndSample
            : RecordOnly;
    }

    private static double Djb2Score(string traceIdHex)
    {
        int hash = 5381;

        foreach (char character in traceIdHex)
        {
            unchecked
            {
                hash = (hash << 5) + hash + character;
            }
        }

        hash = hash == int.MinValue ? int.MaxValue : Math.Abs(hash);

        return (double)hash / int.MaxValue;
    }
}
