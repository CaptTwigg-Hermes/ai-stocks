import hashlib
import json
import re
from dataclasses import dataclass
from datetime import datetime, timedelta
from decimal import ROUND_HALF_UP, Decimal
from typing import Literal

from pydantic import BaseModel, Field, HttpUrl, field_validator
from sqlalchemy import exists, select, text
from sqlalchemy.exc import IntegrityError
from sqlalchemy.orm import Session

from .market import MarketProvider, Quote, SessionWindow
from .models import (
    Agent,
    CorporateAction,
    Fill,
    FinalRanking,
    LedgerEvent,
    Order,
    OrderLifecycleEvent,
    OrderRejection,
    SystemState,
    uid,
)

MONEY = Decimal("0.01")
MIN_OFFICIAL_DELAY = timedelta(minutes=15)
MAX_OFFICIAL_DELAY = timedelta(minutes=20)
CHECKSUM = re.compile(r"^[0-9a-f]{64}$")


def money(value):
    return Decimal(value).quantize(MONEY, rounding=ROUND_HALF_UP)


class TradingError(ValueError):
    pass


class Source(BaseModel):
    url: HttpUrl
    published_at: datetime


class Evidence(BaseModel):
    reason: str = Field(min_length=1, max_length=4000)
    catalyst: str = Field(min_length=1, max_length=4000)
    sources: list[Source] = Field(min_length=1, max_length=20)
    risks: list[str] = Field(min_length=1, max_length=20)
    confidence: Decimal = Field(ge=0, le=1)
    model_id: str = Field(min_length=1)
    decision_at: datetime
    observed_price: Decimal | None = Field(default=None, gt=0)
    observed_portfolio: dict
    research_verification: list[dict[str, object]] = Field(default_factory=list, max_length=20)

    @field_validator("decision_at")
    @classmethod
    def aware(cls, value):
        if value.tzinfo is None:
            raise ValueError("timezone required")
        return value


class OrderRequest(BaseModel):
    decision_id: str = Field(min_length=1, max_length=100)
    agent_id: str
    symbol: str = Field(pattern=r"^[A-Z0-9-]{1,20}$")
    side: Literal["BUY", "SELL"]
    quantity: int = Field(gt=0)
    evidence: Evidence


@dataclass(frozen=True)
class Portfolio:
    cash: Decimal
    holdings: dict[str, int]


@dataclass(frozen=True)
class OrderResult:
    id: str
    status: str
    fill_price: Decimal | None
    fee: Decimal | None
    quote_json: dict | None


@dataclass(frozen=True)
class _FillValues:
    execution: Decimal
    gross: Decimal
    fee: Decimal
    cash_delta: Decimal
    quantity_delta: int
    tier: str
    quote_json: dict


class TradingService:
    def __init__(
        self, session: Session, market: MarketProvider | None, *, auto_commit: bool = True
    ):
        self.s = session
        self.market = market
        self.auto_commit = auto_commit

    def _commit(self) -> None:
        if self.auto_commit:
            self.s.commit()
        else:
            self.s.flush()

    def _mutation_gate(self) -> None:
        if self.s.bind and self.s.bind.dialect.name == "postgresql":
            self.s.execute(text("SELECT pg_advisory_xact_lock(420042)"))
        state = self.s.get(SystemState, 1)
        if state and (state.paused or state.contest_status == "PAUSED"):
            raise TradingError("system paused")

    def portfolio(self, agent_id):
        events = self.s.scalars(
            select(LedgerEvent)
            .where(LedgerEvent.agent_id == agent_id)
            .order_by(LedgerEvent.occurred_at, LedgerEvent.id)
        ).all()
        cash = money(sum((event.cash_delta for event in events), Decimal(0)))
        holdings = {}
        for event in events:
            if event.symbol and event.quantity_delta:
                holdings[event.symbol] = holdings.get(event.symbol, 0) + event.quantity_delta
        return Portfolio(cash, {symbol: qty for symbol, qty in holdings.items() if qty})

    def submit(self, req: OrderRequest, *, observed_at: datetime | None = None):
        req = OrderRequest.model_validate(req.model_dump())
        observed_at = observed_at or req.evidence.decision_at
        if observed_at.tzinfo is None or observed_at.utcoffset() is None:
            raise TradingError("observed_at must be timezone aware")
        if observed_at < req.evidence.decision_at:
            raise TradingError("observed_at cannot precede decision_at")
        request_hash = self._request_hash(req)
        self._mutation_gate()
        self._decision_lock(req.decision_id)
        existing = self.s.scalar(select(Order).where(Order.decision_id == req.decision_id))
        if existing:
            return self._replay(existing, request_hash, req.model_dump(mode="json"))

        agent = self._agent_lock(req.agent_id)
        if not agent:
            raise TradingError("model identity mismatch")
        if agent.model_id != req.evidence.model_id:
            return self._reject_new(req, request_hash, "model identity mismatch")
        state = self.s.get(SystemState, 1)
        if state and state.paused:
            return self._reject_new(req, request_hash, "system paused")
        for source in req.evidence.sources:
            if (
                source.published_at.tzinfo is None
                or source.published_at.utcoffset() is None
                or source.published_at > req.evidence.decision_at
            ):
                return self._reject_new(req, request_hash, "evidence publication timestamp invalid")

        session = self.market.session_containing(req.evidence.decision_at) if self.market else None
        if not session:
            order = self._new_order(req, request_hash, "QUEUED")
            self.s.add(order)
            return self._commit_new(order, request_hash)

        try:
            quote = self._first_valid_quote(
                req.symbol,
                req.evidence.decision_at,
                session,
                as_of=observed_at,
            )
        except TradingError as exc:
            return self._reject_new(req, request_hash, str(exc))
        if quote is None:
            order = self._new_order(req, request_hash, "QUEUED")
            self.s.add(order)
            return self._commit_new(order, request_hash)

        try:
            values = self._calculate_fill(
                req.agent_id,
                req.symbol,
                req.side,
                req.quantity,
                quote,
                session,
                req.evidence.decision_at,
            )
        except TradingError as exc:
            return self._reject_new(req, request_hash, str(exc))
        order = self._new_order(req, request_hash, "FILLED", values)
        self._append_fill(order, values, quote)
        agent.stock_trade_count += 1
        agent.fee_tier = values.tier
        return self._commit_new(order, request_hash)

    def execute_queued(self, at):
        if not self.market:
            return []
        results = []
        examined: set[str] = set()
        while True:
            self._mutation_gate()
            statement = (
                select(Order)
                .where(
                    Order.status == "QUEUED",
                    ~exists(select(Fill.id).where(Fill.order_id == Order.id)),
                    ~exists(select(OrderRejection.id).where(OrderRejection.order_id == Order.id)),
                    ~exists(
                        select(OrderLifecycleEvent.id).where(
                            OrderLifecycleEvent.order_id == Order.id
                        )
                    ),
                )
                .order_by(Order.created_at, Order.id)
                .limit(1)
            )
            if examined:
                statement = statement.where(Order.id.not_in(examined))
            if self.s.bind and self.s.bind.dialect.name == "postgresql":
                statement = statement.with_for_update(skip_locked=True)
            order = self.s.scalar(statement)
            if not order:
                self.s.rollback()
                break
            examined.add(order.id)
            decision_at = order.decision_at
            if decision_at.tzinfo is None:
                decision_at = decision_at.replace(tzinfo=at.tzinfo)
            session = self.market.session_containing(decision_at)
            if session:
                eligible_at = decision_at
            else:
                session = self.market.next_session(decision_at)
                if not session:
                    self.s.rollback()
                    continue
                eligible_at = session.open_at
            try:
                quote = self._first_valid_quote(order.symbol, eligible_at, session, as_of=at)
            except TradingError as exc:
                self._reject_existing(
                    order, order.request_hash, str(exc), at, self._order_payload(order)
                )
                continue
            if not quote:
                self.s.rollback()
                continue
            agent = self._agent_lock(order.agent_id)
            if not agent:
                self._reject_existing(
                    order,
                    order.request_hash,
                    "model identity mismatch",
                    at,
                    self._order_payload(order),
                )
                continue
            try:
                values = self._calculate_fill(
                    order.agent_id,
                    order.symbol,
                    order.side,
                    order.quantity,
                    quote,
                    session,
                    eligible_at,
                )
            except TradingError as exc:
                self._reject_existing(
                    order, order.request_hash, str(exc), at, self._order_payload(order)
                )
                continue
            self._append_fill(order, values, quote)
            agent.stock_trade_count += 1
            agent.fee_tier = values.tier
            self._commit()
            results.append(self._result(order))
        return results

    def _calculate_fill(self, agent_id, symbol, side, quantity, quote, session, eligible_at):
        if self._is_delisted_pending(agent_id, symbol):
            raise TradingError("delisted instrument is frozen pending settlement")
        self._validate_quote(quote, symbol, session)
        if quote.source_at < eligible_at:
            raise TradingError("quote is before eligible execution time")
        if side == "BUY" and quote.history_days < 20:
            raise TradingError("20-day history required")
        raw = quote.price * quantity
        if side == "BUY" and raw > quote.adv20 * Decimal("0.01"):
            raise TradingError("liquidity gate")

        portfolio = self.portfolio(agent_id)
        agent = self.s.get(Agent, agent_id)
        marks = self._verified_marks(portfolio, quote)
        marked_capital = portfolio.cash + sum(
            marks[held_symbol].price * held_quantity
            for held_symbol, held_quantity in portfolio.holdings.items()
        )
        tier = (
            "MINI"
            if agent.fee_tier == "MINI"
            or marked_capital >= Decimal("50000")
            or agent.stock_trade_count >= 500
            else "STARTER"
        )
        slippage = self._slippage_rate(quote, raw)
        execution = quote.price * (
            Decimal(1) + slippage if side == "BUY" else Decimal(1) - slippage
        )
        execution = execution.quantize(Decimal("0.0001"), rounding=ROUND_HALF_UP)
        gross = money(execution * quantity)
        fee = (
            Decimal("0.00")
            if tier == "STARTER"
            else max(Decimal("1.00"), money(gross * Decimal("0.0025")))
        )

        if side == "BUY":
            total = gross + fee
            if total > portfolio.cash:
                raise TradingError("insufficient cash")
            resulting_quantity = portfolio.holdings.get(symbol, 0) + quantity
            target_value = quote.price * resulting_quantity
            post_fill_marked_capital = marked_capital + raw - total
            if post_fill_marked_capital <= 0 or target_value / post_fill_marked_capital > Decimal(
                "0.25"
            ):
                raise TradingError("concentration gate")
            cash_delta = -total
            quantity_delta = quantity
        else:
            if portfolio.holdings.get(symbol, 0) < quantity:
                raise TradingError("insufficient holdings")
            cash_delta = gross - fee
            quantity_delta = -quantity

        quote_json = self._quote_json(quote, slippage=slippage, tier=tier)
        return _FillValues(
            execution=execution,
            gross=gross,
            fee=fee,
            cash_delta=cash_delta,
            quantity_delta=quantity_delta,
            tier=tier,
            quote_json=quote_json,
        )

    def _first_valid_quote(self, symbol, eligible_at, session, as_of=None):
        cursor = eligible_at
        last_error = None
        seen = set()
        for _ in range(1000):
            quote = self.market.first_quote_at_or_after(symbol, cursor, as_of=as_of)
            if quote is None:
                if last_error:
                    raise last_error
                return None
            identity = (quote.source_at, quote.retrieved_at, quote.raw_checksum)
            if identity in seen:
                raise last_error or TradingError("market provider repeated an ineligible quote")
            seen.add(identity)
            try:
                self._validate_quote(quote, symbol, session)
                if quote.source_at < eligible_at:
                    raise TradingError("quote is before eligible execution time")
                return quote
            except TradingError as exc:
                last_error = exc
                cursor = max(cursor, quote.source_at + timedelta(microseconds=1))
        raise TradingError("market provider returned too many invalid quotes")

    def _verified_marks(self, portfolio, execution_quote):
        marks = {}
        for symbol in portfolio.holdings:
            if symbol == execution_quote.symbol:
                mark = execution_quote
            else:
                mark = self.market.latest_quote_at_or_before(
                    symbol, execution_quote.source_at, as_of=execution_quote.retrieved_at
                )
            if mark is None:
                raise TradingError(f"verified market price missing for {symbol}")
            mark_session = self.market.session_containing(mark.source_at)
            if mark_session is None:
                raise TradingError(f"verified market price session missing for {symbol}")
            self._validate_quote(mark, symbol, mark_session)
            marks[symbol] = mark
        return marks

    def _validate_quote(self, quote: Quote, symbol: str, session: SessionWindow):
        if quote.symbol != symbol or not quote.instrument_id.strip():
            raise TradingError("instrument provenance invalid")
        if quote.currency != "SEK":
            raise TradingError("currency provenance invalid")
        if quote.venue != "XSTO":
            raise TradingError("quote venue invalid")
        if quote.price <= 0 or quote.volume <= 0 or quote.adv20 <= 0:
            raise TradingError("quote price, volume, or ADV invalid")
        if quote.source_at.tzinfo is None or quote.retrieved_at.tzinfo is None:
            raise TradingError("quote timestamp invalid")
        if quote.retrieved_at < quote.source_at:
            raise TradingError("quote retrieval timestamp invalid")
        delay = quote.retrieved_at - quote.source_at
        if delay < MIN_OFFICIAL_DELAY or delay > MAX_OFFICIAL_DELAY:
            raise TradingError("quote retrieval delay invalid")
        if not quote.verified:
            raise TradingError("quote is not verified")
        if not CHECKSUM.fullmatch(quote.raw_checksum):
            raise TradingError("quote checksum invalid")
        if quote.raw_evidence_id is not None and not quote.raw_evidence_id.strip():
            raise TradingError("quote raw evidence identity invalid")
        if (
            quote.session_id != session.session_id
            or quote.session_open != session.open_at
            or quote.session_close != session.close_at
            or not session.contains(quote.source_at)
        ):
            raise TradingError("quote session provenance invalid")
        if quote.warning or quote.suspended:
            raise TradingError("quote instrument warning")
        if (quote.bid is None) != (quote.ask is None):
            raise TradingError("quote spread provenance invalid")
        if quote.bid is not None and (quote.bid <= 0 or quote.ask < quote.bid):
            raise TradingError("quote spread provenance invalid")

    @staticmethod
    def _slippage_rate(quote, raw):
        spread_component = Decimal("0.001")
        if quote.bid is not None:
            spread_component = max(
                spread_component, (quote.ask - quote.bid) / (Decimal(2) * quote.price)
            )
        impact = Decimal("0.0025") * (raw / quote.adv20).sqrt()
        return min(Decimal("0.01"), spread_component + impact)

    def _append_fill(self, order, values, quote):
        fill_id = uid()
        ledger_id = uid()
        event = LedgerEvent(
            id=ledger_id,
            agent_id=order.agent_id,
            event_type="FILL",
            symbol=order.symbol,
            cash_delta=values.cash_delta,
            quantity_delta=values.quantity_delta,
            occurred_at=quote.source_at,
            reference_id=f"fill:{order.decision_id}",
            order_id=order.id,
            metadata_json={
                "order_id": order.id,
                "fill_id": fill_id,
                "fee": str(values.fee),
                "side": order.side,
                "quote": values.quote_json,
            },
        )
        fill = Fill(
            id=fill_id,
            order_id=order.id,
            ledger_event_id=ledger_id,
            agent_id=order.agent_id,
            executed_at=quote.source_at,
            fill_price=values.execution,
            gross=values.gross,
            fee=values.fee,
            quote_json=values.quote_json,
            created_at=quote.retrieved_at,
        )
        # The database identity trigger verifies that both the order and its
        # ledger event exist when the fill is inserted. Flush in dependency
        # order while retaining one transaction so any later failure rolls
        # the complete fill back atomically.
        self.s.add(order)
        self.s.flush()
        self.s.add(event)
        self.s.flush()
        self.s.add(fill)

    def _new_order(self, req, request_hash, status, values=None):
        return Order(
            id=uid(),
            decision_id=req.decision_id,
            request_hash=request_hash,
            agent_id=req.agent_id,
            symbol=req.symbol,
            side=req.side,
            quantity=req.quantity,
            status=status,
            decision_at=req.evidence.decision_at,
            created_at=req.evidence.decision_at,
            evidence_json=req.evidence.model_dump(mode="json"),
            quote_json=values.quote_json if values else None,
            fill_price=values.execution if values else None,
            fee=values.fee if values else None,
        )

    def _reject_new(self, req, request_hash, reason):
        order = self._new_order(req, request_hash, "REJECTED")
        order.rejection_reason = reason
        self.s.add(order)
        self.s.add(
            OrderRejection(
                order_id=order.id,
                decision_id=order.decision_id,
                attempted_request_hash=request_hash,
                attempted_request_json=req.model_dump(mode="json"),
                reason=reason,
                rejected_at=req.evidence.decision_at,
            )
        )
        self._commit()
        raise TradingError(reason)

    def _reject_existing(self, order, request_hash, reason, at=None, attempted_json=None):
        found = self.s.scalar(
            select(OrderRejection).where(
                OrderRejection.order_id == order.id,
                OrderRejection.attempted_request_hash == request_hash,
                OrderRejection.reason == reason,
            )
        )
        if not found:
            self.s.add(
                OrderRejection(
                    order_id=order.id,
                    decision_id=order.decision_id,
                    attempted_request_hash=request_hash,
                    attempted_request_json=attempted_json or self._order_payload(order),
                    reason=reason,
                    rejected_at=at or order.decision_at,
                )
            )
        self._commit()

    def _replay(self, order, request_hash, attempted_json=None):
        if order.request_hash != request_hash:
            self._reject_existing(
                order,
                request_hash,
                "idempotency conflict",
                attempted_json=attempted_json,
            )
            raise TradingError("idempotency conflict: decision_id payload differs")
        if order.status == "REJECTED":
            self._commit()
            raise TradingError(order.rejection_reason)
        rejection = self.s.scalar(
            select(OrderRejection)
            .where(
                OrderRejection.order_id == order.id,
                OrderRejection.attempted_request_hash == order.request_hash,
            )
            .order_by(OrderRejection.rejected_at, OrderRejection.id)
        )
        if rejection:
            self._commit()
            raise TradingError(rejection.reason)
        self._commit()
        return self._result(order)

    def _result(self, order):
        lifecycle = self.s.scalar(
            select(OrderLifecycleEvent)
            .where(OrderLifecycleEvent.order_id == order.id)
            .order_by(OrderLifecycleEvent.occurred_at, OrderLifecycleEvent.id)
        )
        if lifecycle:
            return OrderResult(order.id, "CANCELLED", None, None, None)
        fill = self.s.scalar(select(Fill).where(Fill.order_id == order.id))
        if fill:
            return OrderResult(order.id, "FILLED", fill.fill_price, fill.fee, fill.quote_json)
        return OrderResult(order.id, order.status, order.fill_price, order.fee, order.quote_json)

    def _commit_new(self, order, request_hash):
        try:
            self._commit()
        except IntegrityError:
            self.s.rollback()
            existing = self.s.scalar(select(Order).where(Order.decision_id == order.decision_id))
            if not existing:
                raise
            return self._replay(existing, request_hash, self._order_payload(order))
        return self._result(order)

    def order_result(self, order_id):
        order = self.s.get(Order, order_id)
        if order is None:
            raise TradingError("order not found")
        return self._result(order)

    def cancel_order(self, agent_id, order_id, reason, at, request_id):
        payload = {
            "operation": "CANCELLED",
            "agent_id": agent_id,
            "order_id": order_id,
            "reason": reason,
            "at": at.isoformat() if isinstance(at, datetime) else str(at),
        }
        request_hash = self._lifecycle_hash(payload)
        replay = self._lifecycle_replay(agent_id, request_id, request_hash)
        if replay:
            return self.order_result(replay.order_id)
        self._validate_lifecycle_input(reason, at, request_id)
        order = self._queued_order_lock(agent_id, order_id)
        self.s.add(
            OrderLifecycleEvent(
                order_id=order.id,
                agent_id=agent_id,
                event_type="CANCELLED",
                reason=reason,
                occurred_at=at,
                request_id=request_id,
                request_hash=request_hash,
            )
        )
        self._commit()
        return self.order_result(order.id)

    def replace_order(self, agent_id, order_id, replacement, reason, at, request_id):
        replacement = OrderRequest.model_validate(replacement.model_dump())
        payload = {
            "operation": "REPLACED",
            "agent_id": agent_id,
            "order_id": order_id,
            "reason": reason,
            "at": at.isoformat() if isinstance(at, datetime) else str(at),
            "replacement": replacement.model_dump(mode="json"),
        }
        request_hash = self._lifecycle_hash(payload)
        replay = self._lifecycle_replay(agent_id, request_id, request_hash)
        if replay:
            return self.order_result(replay.replacement_order_id)
        self._validate_lifecycle_input(reason, at, request_id)
        original = self._queued_order_lock(agent_id, order_id)
        if replacement.agent_id != agent_id:
            raise TradingError("replacement agent mismatch")
        agent = self._agent_lock(agent_id)
        if agent is None or replacement.evidence.model_id != agent.model_id:
            raise TradingError("model identity mismatch")
        original_decision_at = original.decision_at
        if original_decision_at.tzinfo is None:
            original_decision_at = original_decision_at.replace(
                tzinfo=replacement.evidence.decision_at.tzinfo
            )
        if replacement.evidence.decision_at < original_decision_at:
            raise TradingError("replacement decision precedes original")
        if any(
            source.published_at > replacement.evidence.decision_at
            for source in replacement.evidence.sources
        ):
            raise TradingError("evidence publication timestamp invalid")
        self._decision_lock(replacement.decision_id)
        if self.s.scalar(select(Order).where(Order.decision_id == replacement.decision_id)):
            raise TradingError("replacement decision_id already exists")
        new_order = self._new_order(replacement, self._request_hash(replacement), "QUEUED")
        self.s.add(new_order)
        self.s.flush()
        self.s.add(
            OrderLifecycleEvent(
                order_id=original.id,
                agent_id=agent_id,
                event_type="REPLACED",
                reason=reason,
                occurred_at=at,
                request_id=request_id,
                request_hash=request_hash,
                replacement_order_id=new_order.id,
            )
        )
        self._commit()
        return self.order_result(new_order.id)

    @staticmethod
    def _validate_lifecycle_input(reason, at, request_id):
        if not reason or len(reason) > 4000 or not request_id or len(request_id) > 100:
            raise TradingError("order lifecycle input invalid")
        if at.tzinfo is None or at.utcoffset() is None:
            raise TradingError("order lifecycle timestamp must be timezone aware")

    @staticmethod
    def _lifecycle_hash(payload):
        canonical = json.dumps(payload, sort_keys=True, separators=(",", ":"))
        return hashlib.sha256(canonical.encode()).hexdigest()

    def _lifecycle_replay(self, agent_id, request_id, request_hash):
        existing = self.s.scalar(
            select(OrderLifecycleEvent).where(
                OrderLifecycleEvent.agent_id == agent_id,
                OrderLifecycleEvent.request_id == request_id,
            )
        )
        if existing and existing.request_hash != request_hash:
            raise TradingError("order lifecycle idempotency conflict")
        return existing

    def _queued_order_lock(self, agent_id, order_id):
        statement = select(Order).where(Order.id == order_id, Order.agent_id == agent_id)
        if self.s.bind and self.s.bind.dialect.name == "postgresql":
            statement = statement.with_for_update()
        order = self.s.scalar(statement)
        if order is None:
            raise TradingError("order not found")
        terminal = self.s.scalar(select(Fill.id).where(Fill.order_id == order.id)) or self.s.scalar(
            select(OrderRejection.id).where(OrderRejection.order_id == order.id)
        )
        lifecycle = self.s.scalar(
            select(OrderLifecycleEvent.id).where(OrderLifecycleEvent.order_id == order.id)
        )
        if order.status != "QUEUED" or terminal or lifecycle:
            raise TradingError("order is no longer queued")
        return order

    def _agent_lock(self, agent_id):
        statement = select(Agent).where(Agent.id == agent_id)
        if self.s.bind and self.s.bind.dialect.name == "postgresql":
            statement = statement.with_for_update()
        return self.s.scalar(statement)

    def _decision_lock(self, decision_id):
        if self.s.bind and self.s.bind.dialect.name == "postgresql":
            key = int.from_bytes(
                hashlib.sha256(decision_id.encode()).digest()[:8], "big", signed=True
            )
            self.s.execute(text("SELECT pg_advisory_xact_lock(:key)"), {"key": key})

    @staticmethod
    def _request_hash(req):
        payload = json.dumps(req.model_dump(mode="json"), sort_keys=True, separators=(",", ":"))
        return hashlib.sha256(payload.encode()).hexdigest()

    @staticmethod
    def _order_payload(order):
        return {
            "decision_id": order.decision_id,
            "agent_id": order.agent_id,
            "symbol": order.symbol,
            "side": order.side,
            "quantity": order.quantity,
            "evidence": order.evidence_json,
        }

    @staticmethod
    def _quote_json(quote, *, slippage, tier):
        return {
            "symbol": quote.symbol,
            "instrument_id": quote.instrument_id,
            "price": str(quote.price),
            "source_at": quote.source_at.isoformat(),
            "retrieved_at": quote.retrieved_at.isoformat(),
            "venue": quote.venue,
            "currency": quote.currency,
            "volume": quote.volume,
            "adv20": str(quote.adv20),
            "history_days": quote.history_days,
            "warning": quote.warning,
            "suspended": quote.suspended,
            "bid": str(quote.bid) if quote.bid is not None else None,
            "ask": str(quote.ask) if quote.ask is not None else None,
            "slippage_rate": str(slippage),
            "fee_tier": tier,
            "session_id": quote.session_id,
            "session_open": quote.session_open.isoformat(),
            "session_close": quote.session_close.isoformat(),
            "raw_checksum": quote.raw_checksum,
            "raw_evidence_id": quote.raw_evidence_id,
            "verified": quote.verified,
        }

    def apply_dividend(self, agent_id, symbol, per_share, at, reference):
        per_share = Decimal(per_share)
        if per_share < 0:
            raise TradingError("corporate-action input invalid")
        payload = {"per_share": str(per_share)}
        action = self._begin_corporate_action(agent_id, symbol, "DIVIDEND", at, reference, payload)
        if action is None:
            return
        quantity = self.portfolio(agent_id).holdings.get(symbol, 0)
        self._add_corporate_ledger(
            action, symbol, money(per_share * quantity), 0, "DIVIDEND", "cash"
        )
        self._commit()

    def apply_split(self, agent_id, symbol, ratio, at, reference):
        if type(ratio) is not int or ratio <= 0:
            raise TradingError("corporate-action input invalid")
        payload = {"ratio": ratio}
        action = self._begin_corporate_action(agent_id, symbol, "SPLIT", at, reference, payload)
        if action is None:
            return
        quantity = self.portfolio(agent_id).holdings.get(symbol, 0)
        self._add_corporate_ledger(
            action, symbol, Decimal(0), quantity * (ratio - 1), "SPLIT", "shares"
        )
        self._commit()

    def apply_cash_merger(self, agent_id, symbol, per_share, at, reference):
        per_share = Decimal(per_share)
        if per_share < 0:
            raise TradingError("corporate-action input invalid")
        payload = {"per_share": str(per_share)}
        action = self._begin_corporate_action(
            agent_id, symbol, "CASH_MERGER", at, reference, payload
        )
        if action is None:
            return
        quantity = self.portfolio(agent_id).holdings.get(symbol, 0)
        self._add_corporate_ledger(
            action,
            symbol,
            money(per_share * quantity),
            -quantity,
            "CASH_MERGER",
            "settlement",
        )
        self._commit()

    def apply_stock_merger(
        self, agent_id, symbol, new_symbol, numerator, denominator, at, reference
    ):
        if (
            type(numerator) is not int
            or type(denominator) is not int
            or numerator <= 0
            or denominator <= 0
            or not new_symbol
        ):
            raise TradingError("corporate-action input invalid")
        payload = {
            "new_symbol": new_symbol,
            "numerator": numerator,
            "denominator": denominator,
        }
        action = self._begin_corporate_action(
            agent_id, symbol, "STOCK_MERGER", at, reference, payload
        )
        if action is None:
            return
        quantity = self.portfolio(agent_id).holdings.get(symbol, 0)
        converted, remainder = divmod(quantity * numerator, denominator)
        if remainder:
            self.s.rollback()
            raise TradingError("stock merger would create fractional shares")
        self._add_corporate_ledger(action, symbol, Decimal(0), -quantity, "STOCK_MERGER", "remove")
        self._add_corporate_ledger(action, new_symbol, Decimal(0), converted, "STOCK_MERGER", "add")
        self._commit()

    def apply_delisting(self, agent_id, symbol, official_proceeds, at, reference):
        proceeds = Decimal(official_proceeds) if official_proceeds is not None else None
        if proceeds is not None and proceeds < 0:
            raise TradingError("corporate-action input invalid")
        payload = {"official_proceeds": str(proceeds) if proceeds is not None else None}
        action = self._begin_corporate_action(agent_id, symbol, "DELISTING", at, reference, payload)
        if action is None:
            return
        if proceeds is not None:
            quantity = self.portfolio(agent_id).holdings.get(symbol, 0)
            self._add_corporate_ledger(
                action,
                symbol,
                money(proceeds * quantity),
                -quantity,
                "DELISTING",
                "settlement",
            )
        self._commit()

    def _begin_corporate_action(self, agent_id, symbol, action_type, at, reference, payload):
        self._mutation_gate()
        if (
            not reference
            or len(reference) > 100
            or not symbol
            or at.tzinfo is None
            or at.utcoffset() is None
        ):
            raise TradingError("corporate-action input invalid")
        if self._agent_lock(agent_id) is None:
            raise TradingError("model identity mismatch")
        existing = self.s.scalar(
            select(CorporateAction).where(
                CorporateAction.agent_id == agent_id,
                CorporateAction.reference == reference,
            )
        )
        if existing:
            if (
                existing.action_type != action_type
                or existing.symbol != symbol
                or not self._same_datetime(existing.effective_at, at)
                or existing.payload_json != payload
            ):
                raise TradingError("corporate-action reference conflict")
            self._commit()
            return None
        action = CorporateAction(
            agent_id=agent_id,
            reference=reference,
            action_type=action_type,
            symbol=symbol,
            effective_at=at,
            payload_json=payload,
        )
        self.s.add(action)
        self.s.flush()
        return action

    @staticmethod
    def _same_datetime(left, right):
        if left.tzinfo is None and right.tzinfo is not None:
            left = left.replace(tzinfo=right.tzinfo)
        elif right.tzinfo is None and left.tzinfo is not None:
            right = right.replace(tzinfo=left.tzinfo)
        return left == right

    def _add_corporate_ledger(self, action, symbol, cash_delta, quantity_delta, event_type, suffix):
        self.s.add(
            LedgerEvent(
                agent_id=action.agent_id,
                event_type=event_type,
                symbol=symbol,
                cash_delta=money(cash_delta),
                quantity_delta=quantity_delta,
                occurred_at=action.effective_at,
                reference_id=f"ca:{action.id}:{suffix}",
                metadata_json={
                    "corporate_action_id": action.id,
                    "reference": action.reference,
                    **action.payload_json,
                },
            )
        )

    def _is_delisted_pending(self, agent_id, symbol):
        actions = self.s.scalars(
            select(CorporateAction)
            .where(
                CorporateAction.agent_id == agent_id,
                CorporateAction.symbol == symbol,
                CorporateAction.action_type == "DELISTING",
            )
            .order_by(CorporateAction.effective_at.desc(), CorporateAction.id.desc())
        ).first()
        return bool(actions and actions.payload_json.get("official_proceeds") is None)

    def final_liquidation(self, closing_quotes, at, reference):
        self._mutation_gate()
        if not reference or len(reference) > 100 or at.tzinfo is None or at.utcoffset() is None:
            raise TradingError("final liquidation input invalid")
        quote_payload = {
            symbol: self._final_quote_payload(quote)
            for symbol, quote in sorted(closing_quotes.items())
        }
        input_payload = {
            "reference": reference,
            "at": at.isoformat(),
            "closing_quotes": quote_payload,
        }
        input_hash = hashlib.sha256(
            json.dumps(input_payload, sort_keys=True, separators=(",", ":")).encode()
        ).hexdigest()
        self._finalization_lock(reference)
        existing = self.s.scalars(
            select(FinalRanking)
            .where(FinalRanking.reference == reference)
            .order_by(FinalRanking.rank)
        ).all()
        if existing:
            if any(row.input_hash != input_hash for row in existing):
                raise TradingError("final liquidation reference conflict")
            self._commit()
            return existing

        agents_statement = select(Agent).order_by(Agent.id)
        if self.s.bind and self.s.bind.dialect.name == "postgresql":
            agents_statement = agents_statement.with_for_update()
        agents = self.s.scalars(agents_statement).all()
        values = []
        for agent in agents:
            portfolio = self.portfolio(agent.id)
            marked_capital = portfolio.cash
            for symbol, quantity in portfolio.holdings.items():
                if self._is_delisted_pending(agent.id, symbol):
                    continue
                close = closing_quotes.get(symbol)
                if close is None:
                    raise TradingError(f"official closing quote missing for {symbol}")
                self._validate_closing_quote(close, symbol, at)
                marked_capital += close.price * quantity
            tier = (
                "MINI"
                if agent.fee_tier == "MINI"
                or marked_capital >= Decimal("50000")
                or agent.stock_trade_count >= 500
                else "STARTER"
            )
            liquidation = []
            for symbol, quantity in sorted(portfolio.holdings.items()):
                if self._is_delisted_pending(agent.id, symbol):
                    liquidation.append(
                        {"symbol": symbol, "quantity": quantity, "frozen_value": "0.00"}
                    )
                    continue
                close = closing_quotes[symbol]
                self._validate_closing_quote(close, symbol, at)
                raw = close.price * quantity
                slippage = self._slippage_rate(close, raw)
                execution = (close.price * (Decimal(1) - slippage)).quantize(
                    Decimal("0.0001"), rounding=ROUND_HALF_UP
                )
                gross = money(execution * quantity)
                fee = (
                    Decimal("0.00")
                    if tier == "STARTER"
                    else max(Decimal("1.00"), money(gross * Decimal("0.0025")))
                )
                event_id = uid()
                self.s.add(
                    LedgerEvent(
                        id=event_id,
                        agent_id=agent.id,
                        event_type="FINAL_LIQUIDATION",
                        symbol=symbol,
                        cash_delta=gross - fee,
                        quantity_delta=-quantity,
                        occurred_at=close.source_at,
                        reference_id=f"final:{reference}:{agent.id}:{symbol}"[:100],
                        metadata_json={
                            "reference": reference,
                            "quantity": quantity,
                            "execution_price": str(execution),
                            "gross": str(gross),
                            "fee": str(fee),
                            "quote": self._quote_json(close, slippage=slippage, tier=tier),
                        },
                    )
                )
                liquidation.append(
                    {
                        "symbol": symbol,
                        "quantity": quantity,
                        "execution_price": str(execution),
                        "gross": str(gross),
                        "fee": str(fee),
                    }
                )
                agent.stock_trade_count += 1
            agent.fee_tier = tier
            net_value = money(
                portfolio.cash
                + sum(
                    Decimal(item.get("gross", "0")) - Decimal(item.get("fee", "0"))
                    for item in liquidation
                )
            )
            values.append((agent.id, net_value, liquidation))

        values.sort(key=lambda value: (-value[1], value[0]))
        rows = []
        for rank, (agent_id, net_value, liquidation) in enumerate(values, 1):
            row = FinalRanking(
                reference=reference,
                agent_id=agent_id,
                rank=rank,
                net_liquidation_value=net_value,
                finalized_at=at,
                input_hash=input_hash,
                liquidation_json={"positions": liquidation, "quotes": quote_payload},
            )
            self.s.add(row)
            rows.append(row)
        self._commit()
        return rows

    def _validate_closing_quote(self, quote, symbol, at):
        session = self.market.session_containing(quote.source_at) if self.market else None
        if session is None:
            raise TradingError("official closing quote session missing")
        self._validate_quote(quote, symbol, session)
        if quote.source_at != session.close_at or quote.retrieved_at > at:
            raise TradingError("official closing quote invalid")

    @staticmethod
    def _final_quote_payload(quote):
        return {
            "symbol": quote.symbol,
            "instrument_id": quote.instrument_id,
            "price": str(quote.price),
            "source_at": quote.source_at.isoformat(),
            "retrieved_at": quote.retrieved_at.isoformat(),
            "bid": str(quote.bid) if quote.bid is not None else None,
            "ask": str(quote.ask) if quote.ask is not None else None,
            "adv20": str(quote.adv20),
            "raw_checksum": quote.raw_checksum,
        }

    def _finalization_lock(self, reference):
        if self.s.bind and self.s.bind.dialect.name == "postgresql":
            key = int.from_bytes(
                hashlib.sha256(f"final:{reference}".encode()).digest()[:8], "big", signed=True
            )
            self.s.execute(text("SELECT pg_advisory_xact_lock(:key)"), {"key": key})
