namespace ByteBard.GUSTO;

/// <summary>
/// OpenTelemetry constants for GUSTO job queue instrumentation.
/// </summary>
public static class GustoTelemetry
{
    /// <summary>
    /// The name of the OpenTelemetry ActivitySource for distributed tracing.
    /// Use this when configuring OpenTelemetry: builder.AddSource(GustoTelemetry.ActivitySourceName)
    /// </summary>
    public const string ActivitySourceName = "ByteBard.GUSTO.JobQueue";

    /// <summary>
    /// The name of the OpenTelemetry Meter for metrics.
    /// Use this when configuring OpenTelemetry: builder.AddMeter(GustoTelemetry.MeterName)
    /// </summary>
    public const string MeterName = "ByteBard.GUSTO.JobQueue";
}