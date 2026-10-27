# Decisions: why things are the way they are

Each entry: the decision, the reason, and the trade-off.

## Scope and domain
* **Listings and bids.** Matches the interviewer's domain (vehicle auctions) without being a copy of anything real. Small enough to read in one sitting.
* **Small on purpose.** The goal is a truthful, explainable claim ("I instrumented a .NET service and found a bottleneck with a trace"), not breadth.

## Application
* **.NET 8 minimal API.** Current LTS-era target for the job's stack; the fewest files, so the OpenTelemetry wiring is easy to find in `Program.cs`.
* **Npgsql + Dapper, plain SQL.** The SQL text is exactly what appears in the trace, which makes the diagnosis visible. An ORM would hide the N+1 behind LINQ and add concepts to learn. Trade-off: hand-written SQL.
* **Business rule in `BidRules`.** Pure function, so unit tests need no database or telemetry.
* **`SELECT ... FOR UPDATE` in the bid transaction.** Prevents two concurrent bids from both beating the same "highest bid". Kept because it is a realistic detail; it is not load-tested.
* **`/listings/{id}/shipping` calls the service itself.** The only way to show `HttpClient` instrumentation and context propagation without building a second service. Trade-off: not truly distributed.

## OpenTelemetry
* **OpenTelemetry SDK + OTLP, not a vendor agent.** It is the vendor-neutral standard named in the job description; the app does not know about Jaeger or Prometheus.
* **Export only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set.** Uses the standard env var (configuration, not code) and keeps tests quiet.
* **Auto-instrumentation libraries for ASP.NET Core, HttpClient, Npgsql** give most of the value with no code. Only a few **manual spans** (`search.*`, `bids.place`) add business meaning; manual spans use `ActivitySource` because that is how tracing works in .NET.
* **Custom `Meter`: one counter, one histogram.** Counter for a business event (bids), histogram for latency with a `strategy` tag so slow vs fixed are separable. Metric tags are low-cardinality on purpose (category, strategy), never ids or users.
* **Logs through `ILogger`.** No second logging API; OpenTelemetry attaches trace ids automatically.
* **Npgsql metrics via `AddMeter("Npgsql")`.** Npgsql 8 publishes a standard `Meter` itself, so no extra package is needed.
* **Package versions pinned** (OpenTelemetry 1.20.x, Npgsql 8.0.9). Older 1.9/1.12 triggered a NuGet security advisory warning on the OTLP exporter; 1.20.1 restores clean.

## Infrastructure
* **Collector between app and backends.** Backends can change by editing `otel/collector.yaml` only; this is the pattern used in Kubernetes.
* **Jaeger all-in-one** (in-memory) for traces; **Prometheus + Grafana** for metrics. All free, all common, one container each.
* **No log backend.** Adding Loki would be breadth for little learning value; logs go to the Collector's stdout and this is stated as a limitation.
* **Provisioned Grafana dashboard** (4 panels) so it works with zero clicking. Anonymous admin is acceptable only because it is local.
* **Pinned image tags** so the demo behaves the same next month.
* **Schema and seed live in `db/` and run via Postgres' init directory** rather than app migrations: simple, and the integration test reuses the same schema file.

## The slow query and the fix
* **N+1 with a missing index** was chosen because it is the most common real-world database bottleneck and is clearly visible in a trace (a staircase of identical spans).
* **Fixes measured separately** (batching, then index). Doing both at once would not show which one mattered; the results show each helps and they compound.
* **Separate `/search/slow` route** instead of a feature flag, so before and after can be compared with the same tooling, and the integration test asserts both return identical results (the fix must not change behaviour).
* **Seed data of 400k rows, synthetic.** Large enough for a visible seq-scan cost on a laptop, small enough to load in seconds.
* **Only measured numbers in the README**, with the method and caveats stated.

## Tests and CI
* **xUnit unit tests** for the rule and for the telemetry instruments (they listen with `MeterListener` / `ActivityListener`, the same mechanism the SDK uses).
* **Testcontainers + `WebApplicationFactory`**: a real PostgreSQL, because mocking the DB would not test the SQL, which is the interesting part.
* **GitHub Actions with `permissions: contents: read`, no credentials persisted, pinned to major versions of official actions.** Least privilege; it only builds and tests.

## Not done on purpose
No auth, no sampling config, no alerts, no Kubernetes manifests, no log backend. Each is listed under Limitations rather than half-built.
