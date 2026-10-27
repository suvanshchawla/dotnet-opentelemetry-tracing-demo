using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
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
}
