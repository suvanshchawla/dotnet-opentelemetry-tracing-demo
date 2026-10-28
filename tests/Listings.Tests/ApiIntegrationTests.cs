using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Listings.Api;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Listings.Tests;

/// <summary>
/// Real PostgreSQL in a throw-away Docker container (Testcontainers), real API in-process
/// (WebApplicationFactory). Needs a running Docker daemon.
/// </summary>
public class ApiIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();
        await using var ds = NpgsqlDataSource.Create(_pg.GetConnectionString());
        var schema = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "db", "01-schema.sql"));
        await using var cmd = ds.CreateCommand(schema + @"
            INSERT INTO listings (title, category, reserve_price) VALUES ('Test car', 'cars', 1000), ('Test van', 'vans', 2000);
            INSERT INTO bids (listing_id, bidder, amount) VALUES (1, 'seed', 1200), (1, 'seed2', 1300);");
        await cmd.ExecuteNonQueryAsync();

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.UseSetting("ConnectionStrings:Postgres", _pg.GetConnectionString()));
        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await _pg.DisposeAsync();
    }

    [Fact]
    public async Task Get_listing_returns_it_or_404()
    {
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/listings/1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/listings/999")).StatusCode);
    }

    [Fact]
    public async Task Bid_flow_accepts_higher_and_rejects_lower()
    {
        var ok = await _client.PostAsJsonAsync("/listings/2/bids", new { bidder = "a", amount = 2500 });
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);

        var low = await _client.PostAsJsonAsync("/listings/2/bids", new { bidder = "b", amount = 2500 });
        Assert.Equal(HttpStatusCode.Conflict, low.StatusCode);

        var missing = await _client.PostAsJsonAsync("/listings/999/bids", new { bidder = "b", amount = 5 });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Slow_and_batched_search_return_identical_results()
    {
        var slow = await _client.GetStringAsync("/listings/search/slow?category=cars");
        var fast = await _client.GetStringAsync("/listings/search?category=cars");
        Assert.Equal(slow, fast);                // the "fix" must not change behaviour
        Assert.Contains("\"bidCount\":2", fast); // listing 1 has the two seeded bids
    }

    /// <summary>
    /// Fires many bids at the same moment and checks the invariants that the row lock
    /// (SELECT ... FOR UPDATE in PlaceBidAsync) is supposed to guarantee.
    /// </summary>
    [Fact]
    public async Task Concurrent_bids_on_one_listing_never_break_the_ordering_rules()
    {
        const int n = 20;

        // Phase 1: 20 identical bids on an empty listing (reserve 2000). Exactly one may win;
        // every other one must lose because it does not beat the winner by the minimum increment.
        var same = await FireTogether(Enumerable.Range(0, n).Select(i => (listingId: 2, bidder: $"b{i}", amount: 3000m)));
        Assert.Equal(1, same.Count(c => c == HttpStatusCode.Created));
        Assert.Equal(n - 1, same.Count(c => c == HttpStatusCode.Conflict));
        Assert.Equal([3000m], await StoredAmountsInInsertOrder(2));

        // Phase 2: 20 different amounts on listing 1 (already has seeded bids 1200 and 1300).
        // Any subset may be accepted depending on arrival order, but the stored sequence must be
        // strictly increasing by at least the minimum increment, and every 201 must correspond to a stored row.
        var distinct = await FireTogether(Enumerable.Range(0, n).Select(i => (listingId: 1, bidder: $"d{i}", amount: 2000m + i)));
        Assert.All(distinct, c => Assert.True(c is HttpStatusCode.Created or HttpStatusCode.Conflict));
        var accepted = distinct.Count(c => c == HttpStatusCode.Created);
        Assert.True(accepted >= 1);

        var stored = await StoredAmountsInInsertOrder(1);
        Assert.Equal(2 + accepted, stored.Count); // 2 seeded + accepted ones, nothing lost or duplicated
        for (var i = 1; i < stored.Count; i++)
            Assert.True(stored[i] >= stored[i - 1] + BidRules.MinIncrement,
                $"bid #{i} ({stored[i]}) does not beat the previous one ({stored[i - 1]})");
    }

    /// <summary>Sends all bids after a shared "start" signal so they overlap as much as possible.</summary>
    private async Task<List<HttpStatusCode>> FireTogether(IEnumerable<(int listingId, string bidder, decimal amount)> bids)
    {
        var start = new TaskCompletionSource();
        var tasks = bids.Select(async b =>
        {
            await start.Task;
            var response = await _client.PostAsJsonAsync($"/listings/{b.listingId}/bids", new { b.bidder, b.amount });
            return response.StatusCode;
        }).ToList();
        start.SetResult();
        return (await Task.WhenAll(tasks)).ToList();
    }

    private async Task<List<decimal>> StoredAmountsInInsertOrder(int listingId)
    {
        await using var ds = NpgsqlDataSource.Create(_pg.GetConnectionString());
        await using var cmd = ds.CreateCommand("SELECT amount FROM bids WHERE listing_id = $1 ORDER BY id");
        cmd.Parameters.AddWithValue(listingId);
        await using var reader = await cmd.ExecuteReaderAsync();
        var amounts = new List<decimal>();
        while (await reader.ReadAsync()) amounts.Add(reader.GetDecimal(0));
        return amounts;
    }
}
