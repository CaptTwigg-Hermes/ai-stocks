import csv
from dataclasses import dataclass
from datetime import datetime
from decimal import Decimal, InvalidOperation
from io import StringIO

from .calendar import TradingSession


class NoEligibleTrade(LookupError):
    pass


@dataclass(frozen=True)
class NasdaqTrade:
    isin: str
    price: Decimal
    quantity: int
    executed_at: datetime
    published_at: datetime
    transaction_id: str
    venue: str = "XSTO"


def _time(value: str) -> datetime:
    parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    if parsed.tzinfo is None:
        raise ValueError("Nasdaq timestamps must be timezone-aware")
    return parsed


def parse_first_eligible_trade(
    csv_text: str,
    *,
    isin: str,
    decision_at: datetime,
    observed_at: datetime,
    session: TradingSession,
) -> NasdaqTrade:
    if decision_at.tzinfo is None or observed_at.tzinfo is None:
        raise ValueError("decision and observation timestamps must be timezone-aware")
    if session.open_at.tzinfo is None or session.close_at.tzinfo is None:
        raise ValueError("session timestamps must be timezone-aware")
    lines = csv_text.splitlines()
    if lines and lines[0].strip().lower() == '"sep=;"':
        lines = lines[1:]
    eligible: list[NasdaqTrade] = []
    for row in csv.DictReader(StringIO("\n".join(lines)), delimiter=";"):
        if row.get("Instrument identification code") != isin:
            continue
        if row.get("Venue of execution") != "XSTO":
            continue
        if row.get("Price currency") != "SEK" or row.get("Price notation") != "MONE":
            continue
        try:
            executed_at = _time(row["Trading date and time"])
            published_at = _time(row["Publication date and time"])
            price = Decimal(row["Price"])
            quantity = int(row["Quantity"])
            transaction_id = row["Transaction identification code"].strip()
        except (KeyError, TypeError, ValueError, InvalidOperation):
            continue
        if (
            executed_at < decision_at
            or executed_at < session.open_at
            or executed_at > session.close_at
            or price <= 0
            or quantity <= 0
            or published_at < executed_at
            or published_at > observed_at
            or not transaction_id
        ):
            continue
        eligible.append(
            NasdaqTrade(
                isin=isin,
                price=price,
                quantity=quantity,
                executed_at=executed_at,
                published_at=published_at,
                transaction_id=transaction_id,
            )
        )
    if not eligible:
        raise NoEligibleTrade(f"no eligible XSTO trade for {isin} at or after decision")
    return min(eligible, key=lambda trade: (trade.executed_at, trade.transaction_id))
