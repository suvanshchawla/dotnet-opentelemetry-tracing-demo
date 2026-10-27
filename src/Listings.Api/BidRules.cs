namespace Listings.Api;

/// <summary>Pure business rule, kept free of I/O so it is trivially unit-testable.</summary>
public static class BidRules
{
    public const decimal MinIncrement = 1m;

    /// <returns>null when the bid is acceptable, otherwise a human-readable rejection reason.</returns>
    public static string? Validate(decimal amount, decimal reservePrice, decimal? currentHighest, bool listingOpen)
    {
        if (!listingOpen) return "Listing is closed.";
        if (amount <= 0) return "Bid must be positive.";
        if (currentHighest is null)
            return amount >= reservePrice ? null : $"First bid must be at least the reserve price ({reservePrice}).";
        return amount >= currentHighest + MinIncrement
            ? null
            : $"Bid must beat the current highest bid ({currentHighest}) by at least {MinIncrement}.";
    }
}
