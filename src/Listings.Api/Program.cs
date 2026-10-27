using System.Diagnostics;
using Listings.Api;
using Npgsql;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(NpgsqlDataSource.Create(
    builder.Configuration.GetConnectionString("Postgres")
    ?? "Host=localhost;Username=demo;Password=demo;Database=listings"));
builder.Services.AddSingleton<ListingsRepository>();
builder.Services.AddHttpClient("self", c =>
    c.BaseAddress = new Uri(builder.Configuration["SelfBaseUrl"] ?? "http://localhost:8080"));

// ---------------------------------------------------------------------------------------------
// OpenTelemetry setup. Everything is exported over OTLP (the vendor-neutral wire protocol) to the
// Collector, which then fans out to Jaeger / Prometheus. The OTLP *endpoint* is not hard-coded:
// the SDK reads the standard env var OTEL_EXPORTER_OTLP_ENDPOINT (set in docker-compose.yml).
// If it is not set (e.g. in tests) we skip the exporter so nothing tries to connect.
// ---------------------------------------------------------------------------------------------
var exportOtlp = !string.IsNullOrEmpty(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

builder.Services.AddOpenTelemetry()
    // Resource = "who is sending this data". service.name is what you pick in the Jaeger dropdown.
    .ConfigureResource(r => r.AddService("listings-api", serviceVersion: "1.0.0"))
    .WithTracing(t =>
    {
        t.AddAspNetCoreInstrumentation()   // one server span per incoming request
         .AddHttpClientInstrumentation()   // one client span per outgoing HTTP call + injects `traceparent`
         .AddNpgsql()                      // one span per SQL command
         .AddSource(Telemetry.Name);       // our manual spans (ActivitySource)
        if (exportOtlp) t.AddOtlpExporter();
    })
    .WithMetrics(m =>
    {
        m.AddAspNetCoreInstrumentation()   // http.server.request.duration etc.
         .AddHttpClientInstrumentation()
         .AddMeter("Npgsql")                // Npgsql publishes its own pool/command metrics on a Meter named "Npgsql"
         .AddMeter(Telemetry.Name);        // our custom counter + histogram
        if (exportOtlp) m.AddOtlpExporter();
    });

// Logs: routes ILogger output through OpenTelemetry. Log records written during a request automatically
// carry that request's TraceId/SpanId, which is what lets a backend link a log line to its trace.
builder.Logging.AddOpenTelemetry(o =>
{
    o.IncludeFormattedMessage = true;
    if (exportOtlp) o.AddOtlpExporter();
});

var app = builder.Build();

app.MapGet("/listings/search", async (string? category, int? limit, ListingsRepository repo) =>
    await TimedSearch("batched", () => repo.SearchBatchedAsync(category ?? "cars", Math.Clamp(limit ?? 50, 1, 200))));

// Deliberately slow. Kept as a separate route so the before/after comparison is easy to run.
app.MapGet("/listings/search/slow", async (string? category, int? limit, ListingsRepository repo) =>
    await TimedSearch("nplusone", () => repo.SearchNPlusOneAsync(category ?? "cars", Math.Clamp(limit ?? 50, 1, 200))));

app.MapGet("/listings/{id:int}", async (int id, ListingsRepository repo) =>
    await repo.GetAsync(id) is { } l ? Results.Ok(l) : Results.NotFound());

app.MapPost("/listings/{id:int}/bids", async (int id, BidRequest bid, ListingsRepository repo, ILogger<Program> log) =>
{
    var (outcome, reason) = await repo.PlaceBidAsync(id, bid);
    log.LogInformation("Bid on listing {ListingId} for {Amount}: {Outcome}", id, bid.Amount, outcome);
    return outcome switch
    {
        ListingsRepository.BidOutcome.Accepted => Results.Created($"/listings/{id}", null),
        ListingsRepository.BidOutcome.NotFound => Results.NotFound(),
        _ => Results.Conflict(new { error = reason }),
    };
});

// Context-propagation demo: this endpoint calls ANOTHER endpoint of the same service over HTTP.
// HttpClient instrumentation adds a `traceparent` header; ASP.NET Core instrumentation on the receiving
// side reads it. Result: ONE trace containing two server spans, joined by a client span.
app.MapGet("/listings/{id:int}/shipping", async (int id, IHttpClientFactory http) =>
{
    var rate = await http.CreateClient("self").GetFromJsonAsync<ShippingRate>($"/internal/shipping-rate?listingId={id}");
    return Results.Ok(new { listingId = id, rate });
});
app.MapGet("/internal/shipping-rate", (int listingId) => new ShippingRate("EUR", 10m + listingId % 7));

app.MapGet("/healthz", () => "ok");

app.Run();

static async Task<IResult> TimedSearch(string strategy, Func<Task<List<ListingSummary>>> search)
{
    var sw = Stopwatch.StartNew();
    var result = await search();
    Telemetry.SearchDuration.Record(sw.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("strategy", strategy));
    return Results.Ok(result);
}

record ShippingRate(string Currency, decimal Amount);

// Makes Program visible to WebApplicationFactory<Program> in the integration tests.
public partial class Program;
