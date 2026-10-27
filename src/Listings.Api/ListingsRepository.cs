using System.Diagnostics;
using Dapper;
using Npgsql;

namespace Listings.Api;

public record Listing(int Id, string Title, string Category, decimal ReservePrice, bool IsOpen);
public record ListingSummary(int Id, string Title, decimal? HighestBid, long BidCount);
public record BidRequest(string Bidder, decimal Amount);

/// <summary>
/// Plain SQL + Dapper. We do NOT write any tracing code for the SQL itself: the Npgsql instrumentation
/// (registered in Program.cs) creates one span per command automatically, with the SQL text attached.
/// </summary>
public class ListingsRepository(NpgsqlDataSource db)
{
    public async Task<Listing?> GetAsync(int id)
    {
        await using var conn = await db.OpenConnectionAsync();
        return await conn.QuerySingleOrDefaultAsync<Listing>(
            "SELECT id, title, category, reserve_price AS ReservePrice, is_open AS IsOpen FROM listings WHERE id = @id",
            new { id });
    }

    /// <summary>
    /// SLOW VERSION (the "before"): the classic N+1. One query for the listings, then one more query
    /// per listing for its bid stats. 50 listings => 51 round trips, and each one scans the whole bids
    /// table until the index in db/add-index.sql exists. In Jaeger this shows up as a long "staircase"
    /// of tiny identical SELECT spans.
    /// </summary>
    public async Task<List<ListingSummary>> SearchNPlusOneAsync(string category, int limit)
    {
        // Manual span: groups the work under a name *we* chose, so the trace reads as a story.
        using var span = Telemetry.Source.StartActivity("search.nplusone");
        span?.SetTag("search.category", category);

        await using var conn = await db.OpenConnectionAsync();
        var listings = (await conn.QueryAsync<(int Id, string Title)>(
            "SELECT id, title FROM listings WHERE category = @category ORDER BY id LIMIT @limit",
            new { category, limit })).ToList();

        var result = new List<ListingSummary>();
        foreach (var l in listings)
        {
            var stats = await conn.QuerySingleAsync<(decimal? Max, long Count)>(
                "SELECT max(amount), count(*) FROM bids WHERE listing_id = @id", new { id = l.Id });
            result.Add(new ListingSummary(l.Id, l.Title, stats.Max, stats.Count));
        }
        span?.SetTag("search.result_count", result.Count);
        return result;
    }

    /// <summary>
    /// FIXED VERSION (the "after"): two queries total, no matter how many listings. The bid stats for all
    /// listings come from a single GROUP BY ... WHERE listing_id = ANY(@ids). With the index in place that
    /// is an index/bitmap scan instead of a full table scan.
    /// </summary>
    public async Task<List<ListingSummary>> SearchBatchedAsync(string category, int limit)
    {
        using var span = Telemetry.Source.StartActivity("search.batched");
        span?.SetTag("search.category", category);

        await using var conn = await db.OpenConnectionAsync();
        var listings = (await conn.QueryAsync<(int Id, string Title)>(
            "SELECT id, title FROM listings WHERE category = @category ORDER BY id LIMIT @limit",
            new { category, limit })).ToList();

        var ids = listings.Select(l => l.Id).ToArray();
        var stats = (await conn.QueryAsync<(int ListingId, decimal Max, long Count)>(
                "SELECT listing_id, max(amount), count(*) FROM bids WHERE listing_id = ANY(@ids) GROUP BY listing_id",
                new { ids }))
            .ToDictionary(s => s.ListingId);

        var result = listings
            .Select(l => stats.TryGetValue(l.Id, out var s)
                ? new ListingSummary(l.Id, l.Title, s.Max, s.Count)
                : new ListingSummary(l.Id, l.Title, null, 0))
            .ToList();
        span?.SetTag("search.result_count", result.Count);
        return result;
    }

    public enum BidOutcome { Accepted, NotFound, Rejected }

    public async Task<(BidOutcome Outcome, string? Reason)> PlaceBidAsync(int listingId, BidRequest bid)
    {
        // Manual span with business attributes. Note: keep attributes low-risk (no personal data).
        using var span = Telemetry.Source.StartActivity("bids.place");
        span?.SetTag("listing.id", listingId);
        span?.SetTag("bid.amount", (double)bid.Amount);

        await using var conn = await db.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();

        // FOR UPDATE locks the listing row so two concurrent bids cannot both beat the same "highest bid".
        var listing = await conn.QuerySingleOrDefaultAsync<Listing>(
            "SELECT id, title, category, reserve_price AS ReservePrice, is_open AS IsOpen FROM listings WHERE id = @listingId FOR UPDATE",
            new { listingId }, tx);
        if (listing is null) return (BidOutcome.NotFound, null);

        var highest = await conn.ExecuteScalarAsync<decimal?>(
            "SELECT max(amount) FROM bids WHERE listing_id = @listingId", new { listingId }, tx);

        var rejection = BidRules.Validate(bid.Amount, listing.ReservePrice, highest, listing.IsOpen);
        if (rejection is not null)
        {
            span?.SetTag("bid.accepted", false);
            return (BidOutcome.Rejected, rejection);
        }

        await conn.ExecuteAsync(
            "INSERT INTO bids (listing_id, bidder, amount) VALUES (@listingId, @Bidder, @Amount)",
            new { listingId, bid.Bidder, bid.Amount }, tx);
        await tx.CommitAsync();

        span?.SetTag("bid.accepted", true);
        Telemetry.BidsPlaced.Add(1, new KeyValuePair<string, object?>("listing.category", listing.Category));
        return (BidOutcome.Accepted, null);
    }
}
