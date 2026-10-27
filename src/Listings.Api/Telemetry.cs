using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Listings.Api;

/// <summary>
/// Our own telemetry "instruments". In .NET, OpenTelemetry does not have its own tracing/metrics API:
///  - a trace span is a System.Diagnostics.Activity, created from an ActivitySource
///  - a metric is a System.Diagnostics.Metrics instrument, created from a Meter
/// The OpenTelemetry SDK (configured in Program.cs) just *listens* to these by name and exports them.
/// That is why Program.cs calls AddSource("Listings.Api") and AddMeter("Listings.Api").
/// </summary>
public static class Telemetry
{
    public const string Name = "Listings.Api";

    // Source of manual spans. StartActivity() returns null when nobody listens, so always use `?.`.
    public static readonly ActivitySource Source = new(Name);

    private static readonly Meter Meter = new(Name);

    // Counter: only goes up. Prometheus turns it into a rate with rate(...).
    public static readonly Counter<long> BidsPlaced =
        Meter.CreateCounter<long>("listings.bids.placed", unit: "{bid}", description: "Accepted bids");

    // Histogram: records a distribution (latency percentiles). The tag lets us split slow vs batched.
    public static readonly Histogram<double> SearchDuration =
        Meter.CreateHistogram<double>("listings.search.duration", unit: "ms",
            description: "Time spent building a search response, by strategy");
}
