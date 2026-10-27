-- Deliberately NO index on bids(listing_id): that is the "before" state of the demo.
CREATE TABLE listings (
    id            serial PRIMARY KEY,
    title         text    NOT NULL,
    category      text    NOT NULL,
    reserve_price numeric(12,2) NOT NULL,
    is_open       boolean NOT NULL DEFAULT true
);

CREATE TABLE bids (
    id         bigserial PRIMARY KEY,
    listing_id int     NOT NULL REFERENCES listings(id),
    bidder     text    NOT NULL,
    amount     numeric(12,2) NOT NULL,
    placed_at  timestamptz NOT NULL DEFAULT now()
);
