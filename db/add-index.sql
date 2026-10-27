-- THE FIX (part 2). Run this by hand after you have seen the slow trace:
--   docker compose exec postgres psql -U demo -d listings -f /db/add-index.sql
CREATE INDEX IF NOT EXISTS bids_listing_id_idx ON bids (listing_id);
ANALYZE bids;
