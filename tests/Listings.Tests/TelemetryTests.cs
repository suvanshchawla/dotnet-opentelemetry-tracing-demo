using System.Diagnostics;
using System.Diagnostics.Metrics;
using Listings.Api;

namespace Listings.Tests;

/// <summary>
/// Shows how the OpenTelemetry SDK "sees" our instruments: it attaches an ActivityListener / MeterListener
/// by name. We do the same thing by hand here, which is also a good way to learn what the SDK does.
/// </summary>
public class TelemetryTests
{
    [Fact]
    public void Bid_counter_records_value_with_category_tag()
    {
        var measurements = new List<(long Value, string? Category)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (inst, l) =>
        {
            if (inst.Meter.Name == Telemetry.Name && inst.Name == "listings.bids.placed") l.EnableMeasurementEvents(inst);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            measurements.Add((value, tags.ToArray().FirstOrDefault(t => t.Key == "listing.category").Value as string)));
        listener.Start();

        Telemetry.BidsPlaced.Add(1, new KeyValuePair<string, object?>("listing.category", "cars"));

        Assert.Single(measurements, (1L, "cars"));
    }

    [Fact]
    public void Manual_span_is_only_created_when_someone_listens()
    {
        Assert.Null(Telemetry.Source.StartActivity("nobody-listens")); // no listener => null => `?.` calls are free

        var started = new List<string>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == Telemetry.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = a => started.Add(a.OperationName),
        };
        ActivitySource.AddActivityListener(listener);

        using (Telemetry.Source.StartActivity("bids.place")) { }

        Assert.Contains("bids.place", started);
    }
}
