using Listings.Api;

namespace Listings.Tests;

public class BidRulesTests
{
    [Fact] public void First_bid_below_reserve_is_rejected() =>
        Assert.NotNull(BidRules.Validate(500, reservePrice: 1000, currentHighest: null, listingOpen: true));

    [Fact] public void First_bid_at_reserve_is_accepted() =>
        Assert.Null(BidRules.Validate(1000, 1000, null, true));

    [Fact] public void Bid_must_beat_highest_by_min_increment() =>
        Assert.NotNull(BidRules.Validate(1500, 1000, currentHighest: 1500, listingOpen: true));

    [Fact] public void Bid_beating_highest_is_accepted() =>
        Assert.Null(BidRules.Validate(1501, 1000, 1500, true));

    [Fact] public void Closed_listing_rejects_everything() =>
        Assert.Equal("Listing is closed.", BidRules.Validate(99999, 1000, null, listingOpen: false));

    [Fact] public void Non_positive_bid_is_rejected() =>
        Assert.NotNull(BidRules.Validate(0, 0, null, true));
}
