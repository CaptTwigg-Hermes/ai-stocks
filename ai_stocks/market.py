from dataclasses import dataclass
from datetime import datetime
from decimal import Decimal
from typing import Protocol


@dataclass(frozen=True)
class SessionWindow:
    open_at: datetime
    close_at: datetime
    session_id: str

    def contains(self, at: datetime) -> bool:
        return self.open_at <= at <= self.close_at


@dataclass(frozen=True)
class Quote:
    symbol: str
    instrument_id: str
    price: Decimal
    source_at: datetime
    retrieved_at: datetime
    venue: str
    currency: str
    volume: int
    adv20: Decimal
    history_days: int
    warning: bool
    suspended: bool
    session_id: str
    session_open: datetime
    session_close: datetime
    raw_checksum: str
    verified: bool
    raw_evidence_id: str | None = None
    bid: Decimal | None = None
    ask: Decimal | None = None


class MarketProvider(Protocol):
    def session_containing(self, at: datetime) -> SessionWindow | None: ...

    def next_session(self, at: datetime) -> SessionWindow | None: ...

    def first_quote_at_or_after(
        self, symbol: str, at: datetime, *, as_of: datetime | None = None
    ) -> Quote | None: ...

    def latest_quote_at_or_before(
        self, symbol: str, at: datetime, *, as_of: datetime | None = None
    ) -> Quote | None: ...


class FakeMarket:
    def __init__(
        self,
        quotes: list[Quote],
        sessions: list[SessionWindow] | None = None,
        is_open: bool | None = None,
    ):
        self.quotes = list(quotes)
        if sessions is None:
            session_keys = sorted({(q.session_open, q.session_close, q.session_id) for q in quotes})
            self.sessions = [SessionWindow(*session) for session in session_keys]
        else:
            self.sessions = sorted(sessions, key=lambda session: session.open_at)
        # Kept only as a compatibility hint for callers constructing an empty fake.
        # TradingService never trusts this boolean for session eligibility.
        self.is_open = is_open

    def add(self, q: Quote):
        self.quotes.append(q)
        if not any(session.session_id == q.session_id for session in self.sessions):
            self.sessions.append(SessionWindow(q.session_open, q.session_close, q.session_id))
            self.sessions.sort(key=lambda session: session.open_at)

    def session_containing(self, at):
        return next((session for session in self.sessions if session.contains(at)), None)

    def next_session(self, at):
        return next((session for session in self.sessions if session.open_at > at), None)

    def first_quote_at_or_after(self, symbol, at, *, as_of=None):
        quotes = [
            quote
            for quote in self.quotes
            if quote.symbol == symbol
            and quote.source_at >= at
            and (as_of is None or quote.retrieved_at <= as_of)
        ]
        return (
            min(quotes, key=lambda quote: (quote.source_at, quote.retrieved_at)) if quotes else None
        )

    def latest_quote_at_or_before(self, symbol, at, *, as_of=None):
        quotes = [
            quote
            for quote in self.quotes
            if quote.symbol == symbol
            and quote.source_at <= at
            and (as_of is None or quote.retrieved_at <= as_of)
        ]
        return (
            max(quotes, key=lambda quote: (quote.source_at, quote.retrieved_at)) if quotes else None
        )
