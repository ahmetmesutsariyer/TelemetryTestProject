namespace TelemetryTest.Api.Lab;

/// <summary>Which telemetry set-up this run measures. <c>Lab:Variant</c>, one per launch profile.</summary>
public enum LabVariant
{
    /// <summary>A. The production set-up as it is: the distro, the ratio, nothing else.</summary>
    Distro,

    /// <summary>B. The same settings handed to the Azure Monitor package the distro is built on.</summary>
    Control,

    /// <summary>C. The production set-up, then the exporter's sampler set again after the distro.</summary>
    Fixed,
}

internal static class LabVariants
{
    public static LabVariant From(IConfiguration configuration) =>
        Enum.TryParse(configuration["Lab:Variant"], ignoreCase: true, out LabVariant variant)
            ? variant
            : throw new InvalidOperationException("Lab:Variant must be Distro, Control or Fixed.");
}
