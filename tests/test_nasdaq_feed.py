from datetime import UTC, date, datetime
from decimal import Decimal

import pytest

from ai_stocks.calendar import session_for
from ai_stocks.nasdaq_feed import NoEligibleTrade, parse_first_eligible_trade

CSV = """"sep=;"
Trading date and time;Instrument identification code;Price;Missing Price;Price currency;Price notation;Quantity;Venue of execution;Trading system;Publication date and time;Venue of publication;Transaction identification code;Flags
2026-08-06T09:59:59.000000Z;SE0000108656;96.90;;SEK;MONE;10;XSTO;CLOB;2026-08-06T10:14:59.000000Z;XSTO;1;---
2026-08-06T10:00:01.000000Z;SE0000108656;97.00;;SEK;MONE;20;XSTO;CLOB;2026-08-06T10:15:01.000000Z;XSTO;2;---
2026-08-06T10:00:00.500000Z;SE0000108656;96.95;;SEK;MONE;15;XSTO;CLOB;2026-08-06T10:15:00.500000Z;XSTO;3;---
"""

SESSION = session_for(date(2026, 8, 6))
assert SESSION is not None
OBSERVED = datetime(2026, 8, 6, 10, 16, tzinfo=UTC)


def test_returns_earliest_xsto_trade_at_or_after_decision():
    trade = parse_first_eligible_trade(
        CSV,
        isin="SE0000108656",
        decision_at=datetime(2026, 8, 6, 10, 0, tzinfo=UTC),
        observed_at=OBSERVED,
        session=SESSION,
    )

    assert trade.executed_at == datetime(2026, 8, 6, 10, 0, 0, 500000, tzinfo=UTC)
    assert trade.price == Decimal("96.95")
    assert trade.quantity == 15
    assert trade.transaction_id == "3"


def test_rejects_when_no_post_decision_xsto_trade_exists():
    with pytest.raises(NoEligibleTrade):
        parse_first_eligible_trade(
            CSV,
            isin="SE9999999999",
            decision_at=datetime(2026, 8, 6, 10, 0, tzinfo=UTC),
            observed_at=OBSERVED,
            session=SESSION,
        )


def test_rejects_trades_outside_the_exact_stockholm_session():
    outside = """"sep=;"
Trading date and time;Instrument identification code;Price;Missing Price;Price currency;Price notation;Quantity;Venue of execution;Trading system;Publication date and time;Venue of publication;Transaction identification code;Flags
2026-08-06T06:59:59Z;SE0000108656;96.00;;SEK;MONE;1;XSTO;CLOB;2026-08-06T07:14:59Z;XSTO;before;---
2026-08-06T15:30:00.000001Z;SE0000108656;97.00;;SEK;MONE;1;XSTO;CLOB;2026-08-06T15:45:00.000001Z;XSTO;after;---
"""
    with pytest.raises(NoEligibleTrade):
        parse_first_eligible_trade(
            outside,
            isin="SE0000108656",
            decision_at=datetime(2026, 8, 6, 6, tzinfo=UTC),
            observed_at=datetime(2026, 8, 6, 16, tzinfo=UTC),
            session=SESSION,
        )


def test_cannot_use_a_trade_before_its_publication_was_observed():
    with pytest.raises(NoEligibleTrade):
        parse_first_eligible_trade(
            CSV,
            isin="SE0000108656",
            decision_at=datetime(2026, 8, 6, 10, 0, tzinfo=UTC),
            observed_at=datetime(2026, 8, 6, 10, 15, tzinfo=UTC),
            session=SESSION,
        )
