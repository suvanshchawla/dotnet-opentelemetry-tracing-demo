-- Synthetic data only: 2,000 listings (4 categories) and ~400,000 bids.
INSERT INTO listings (title, category, reserve_price)
SELECT 'Listing #' || g,
       (ARRAY['cars','vans','trucks','bikes'])[1 + g % 4],
       1000 + (g % 50) * 100
FROM generate_series(1, 2000) g;

INSERT INTO bids (listing_id, bidder, amount)
SELECT 1 + (g % 2000), 'bidder-' || (g % 500), 1000 + (g % 9000) + (g / 2000)
FROM generate_series(1, 400000) g;

ANALYZE;
