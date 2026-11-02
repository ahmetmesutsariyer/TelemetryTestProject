using System.Globalization;

namespace TelemetryTest.Api.Infrastructure.Observability;

/// <summary>Whether the API sends telemetry to Application Insights, under which name, and how much of it.</summary>
/// <remarks>
/// <para>
/// The connection string is read from <c>APPLICATIONINSIGHTS_CONNECTION_STRING</c>, the variable Microsoft's distro and
/// its documentation name. The environment sets it; <c>appsettings.json</c> carries the key empty only so the
/// setting is visible there. Empty means off, which is how a developer's machine and the test suite stay silent.
/// </para>
/// <para>
/// A value that is set but is not a connection string stops the API and names the variable, never the value. The
/// exporter would refuse it too, but with a message that names neither the variable nor where it came from.
/// </para>
/// </remarks>
internal sealed record TelemetrySettings(string? ConnectionString, double SamplingRatio, string ServiceName)
{
    public const string ConnectionStringKey = "APPLICATIONINSIGHTS_CONNECTION_STRING";

    public const string SamplingRatioKey = "OpenTelemetry:SamplingRatio";

    public const string ServiceNameKey = "OpenTelemetry:ServiceName";

    public bool SendsToApplicationInsights => ConnectionString is not null;

    public static TelemetrySettings From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        string? connectionString = configuration[ConnectionStringKey];

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            connectionString = null;
        }
        else if (!connectionString.Contains("InstrumentationKey=", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{ConnectionStringKey} is set but is not an Application Insights connection string: it carries no "
                + "InstrumentationKey. Copy it from the resource's Overview, or leave it empty to send no telemetry.");
        }

        return new TelemetrySettings(
            connectionString,
            ReadRatio(configuration[SamplingRatioKey]),
            configuration[ServiceNameKey] is { Length: > 0 } name ? name : "TelemetryTest.Api");
    }

    private static double ReadRatio(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 1.0;
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double ratio)
            && ratio is >= 0.0 and <= 1.0)
        {
            return ratio;
        }

        throw new InvalidOperationException(
            $"{SamplingRatioKey} must be a number from 0 to 1, the share of traces kept; it is '{value}'.");
    }
}
