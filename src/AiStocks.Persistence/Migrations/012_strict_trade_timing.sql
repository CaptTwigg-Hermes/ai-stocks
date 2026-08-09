ALTER TABLE market_strict_trade_rows
    DROP CONSTRAINT IF EXISTS market_strict_trade_rows_check;

ALTER TABLE market_strict_trade_rows
    ADD CONSTRAINT market_strict_trade_rows_check
    CHECK (
        published_at >= traded_at - interval '1 second'
        AND retrieved_at >= traded_at + interval '15 minutes'
    );
