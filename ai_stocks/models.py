from datetime import datetime
from decimal import Decimal
from uuid import uuid4

from sqlalchemy import (
    JSON,
    Boolean,
    CheckConstraint,
    DateTime,
    ForeignKey,
    Integer,
    Numeric,
    String,
    UniqueConstraint,
    event,
)
from sqlalchemy.orm import Mapped, mapped_column

from .db import Base


def uid():
    return str(uuid4())


class Agent(Base):
    __tablename__ = "agents"
    __table_args__ = (
        CheckConstraint("initial_cash = 30000.00", name="ck_agents_initial_cash"),
        CheckConstraint(
            "model_id IN ('gpt-5.6-sol', 'claude-opus-4.8', 'claude-sonnet-5', "
            "'gemini-3.1-pro-preview')",
            name="ck_agents_model_id",
        ),
        CheckConstraint("fee_tier IN ('STARTER', 'MINI')", name="ck_agents_fee_tier"),
        CheckConstraint("stock_trade_count >= 0", name="ck_agents_trade_count"),
    )
    id: Mapped[str] = mapped_column(String(40), primary_key=True)
    model_id: Mapped[str] = mapped_column(String(100), unique=True)
    initial_cash: Mapped[Decimal] = mapped_column(Numeric(18, 2))
    fee_tier: Mapped[str] = mapped_column(String(20), default="STARTER")
    stock_trade_count: Mapped[int] = mapped_column(Integer, default=0)


class LedgerEvent(Base):
    __tablename__ = "ledger_events"
    __table_args__ = (UniqueConstraint("agent_id", "reference_id"),)
    id: Mapped[str] = mapped_column(String(40), primary_key=True, default=uid)
    agent_id: Mapped[str] = mapped_column(ForeignKey("agents.id"), index=True)
    event_type: Mapped[str] = mapped_column(String(40), index=True)
    symbol: Mapped[str | None] = mapped_column(String(30), nullable=True)
    cash_delta: Mapped[Decimal] = mapped_column(Numeric(18, 2), default=Decimal("0"))
    quantity_delta: Mapped[int] = mapped_column(Integer, default=0)
    occurred_at: Mapped[datetime] = mapped_column(DateTime(timezone=True))
    reference_id: Mapped[str | None] = mapped_column(String(100), nullable=True)
    order_id: Mapped[str | None] = mapped_column(
        ForeignKey("orders.id", deferrable=True, initially="DEFERRED"), nullable=True, index=True
    )
    metadata_json: Mapped[dict] = mapped_column(JSON, default=dict)


class Order(Base):
    __tablename__ = "orders"
    __table_args__ = (
        UniqueConstraint("decision_id"),
        CheckConstraint("side IN ('BUY', 'SELL')", name="ck_orders_side"),
        CheckConstraint("quantity > 0", name="ck_orders_quantity"),
        CheckConstraint(
            "status IN ('QUEUED', 'FILLED', 'REJECTED', 'CANCELLED')",
            name="ck_orders_status",
        ),
        CheckConstraint("length(request_hash) = 64", name="ck_orders_request_hash"),
    )
    id: Mapped[str] = mapped_column(String(40), primary_key=True, default=uid)
    decision_id: Mapped[str] = mapped_column(String(100))
    request_hash: Mapped[str] = mapped_column(String(64))
    agent_id: Mapped[str] = mapped_column(ForeignKey("agents.id"), index=True)
    symbol: Mapped[str] = mapped_column(String(30))
    side: Mapped[str] = mapped_column(String(4))
    quantity: Mapped[int] = mapped_column(Integer)
    status: Mapped[str] = mapped_column(String(20))
    decision_at: Mapped[datetime] = mapped_column(DateTime(timezone=True))
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True))
    evidence_json: Mapped[dict] = mapped_column(JSON)
    quote_json: Mapped[dict | None] = mapped_column(JSON, nullable=True)
    fill_price: Mapped[Decimal | None] = mapped_column(Numeric(18, 4), nullable=True)
    fee: Mapped[Decimal | None] = mapped_column(Numeric(18, 2), nullable=True)
    rejection_reason: Mapped[str | None] = mapped_column(String(200), nullable=True)


class Fill(Base):
    __tablename__ = "fills"
    __table_args__ = (
        CheckConstraint("fill_price > 0", name="ck_fills_price"),
        CheckConstraint("gross > 0", name="ck_fills_gross"),
        CheckConstraint("fee >= 0", name="ck_fills_fee"),
    )
    id: Mapped[str] = mapped_column(String(40), primary_key=True, default=uid)
    order_id: Mapped[str] = mapped_column(ForeignKey("orders.id"), unique=True, index=True)
    ledger_event_id: Mapped[str] = mapped_column(ForeignKey("ledger_events.id"), unique=True)
    agent_id: Mapped[str] = mapped_column(ForeignKey("agents.id"), index=True)
    executed_at: Mapped[datetime] = mapped_column(DateTime(timezone=True))
    fill_price: Mapped[Decimal] = mapped_column(Numeric(18, 4))
    gross: Mapped[Decimal] = mapped_column(Numeric(18, 2))
    fee: Mapped[Decimal] = mapped_column(Numeric(18, 2))
    quote_json: Mapped[dict] = mapped_column(JSON)
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True))


class OrderRejection(Base):
    __tablename__ = "order_rejections"
    __table_args__ = (UniqueConstraint("order_id", "attempted_request_hash", "reason"),)
    id: Mapped[str] = mapped_column(String(40), primary_key=True, default=uid)
    order_id: Mapped[str] = mapped_column(ForeignKey("orders.id"), index=True)
    decision_id: Mapped[str] = mapped_column(String(100), index=True)
    attempted_request_hash: Mapped[str] = mapped_column(String(64))
    attempted_request_json: Mapped[dict] = mapped_column(JSON)
    reason: Mapped[str] = mapped_column(String(200))
    rejected_at: Mapped[datetime] = mapped_column(DateTime(timezone=True))


class OrderLifecycleEvent(Base):
    __tablename__ = "order_lifecycle_events"
    __table_args__ = (
        UniqueConstraint("agent_id", "request_id"),
        CheckConstraint("event_type IN ('CANCELLED', 'REPLACED')", name="ck_order_lifecycle_type"),
        CheckConstraint("length(request_hash) = 64", name="ck_order_lifecycle_hash"),
    )
    id: Mapped[str] = mapped_column(String(40), primary_key=True, default=uid)
    order_id: Mapped[str] = mapped_column(ForeignKey("orders.id"), index=True)
    agent_id: Mapped[str] = mapped_column(ForeignKey("agents.id"), index=True)
    event_type: Mapped[str] = mapped_column(String(20))
    reason: Mapped[str] = mapped_column(String(4000))
    occurred_at: Mapped[datetime] = mapped_column(DateTime(timezone=True))
    request_id: Mapped[str] = mapped_column(String(100))
    request_hash: Mapped[str] = mapped_column(String(64))
    replacement_order_id: Mapped[str | None] = mapped_column(ForeignKey("orders.id"), nullable=True)


class CorporateAction(Base):
    __tablename__ = "corporate_actions"
    __table_args__ = (
        UniqueConstraint("agent_id", "reference"),
        CheckConstraint(
            "action_type IN ('DIVIDEND', 'SPLIT', 'CASH_MERGER', 'STOCK_MERGER', 'DELISTING')",
            name="ck_corporate_action_type",
        ),
    )
    id: Mapped[str] = mapped_column(String(40), primary_key=True, default=uid)
    agent_id: Mapped[str] = mapped_column(ForeignKey("agents.id"), index=True)
    reference: Mapped[str] = mapped_column(String(100))
    action_type: Mapped[str] = mapped_column(String(30), index=True)
    symbol: Mapped[str] = mapped_column(String(30), index=True)
    effective_at: Mapped[datetime] = mapped_column(DateTime(timezone=True))
    payload_json: Mapped[dict] = mapped_column(JSON)


class FinalRanking(Base):
    __tablename__ = "final_rankings"
    __table_args__ = (
        UniqueConstraint("reference", "agent_id"),
        UniqueConstraint("reference", "rank"),
        CheckConstraint("rank > 0", name="ck_final_rank_positive"),
        CheckConstraint("net_liquidation_value >= 0", name="ck_final_value_nonnegative"),
        CheckConstraint("length(input_hash) = 64", name="ck_final_input_hash"),
    )
    id: Mapped[str] = mapped_column(String(40), primary_key=True, default=uid)
    reference: Mapped[str] = mapped_column(String(100), index=True)
    agent_id: Mapped[str] = mapped_column(ForeignKey("agents.id"), index=True)
    rank: Mapped[int] = mapped_column(Integer)
    net_liquidation_value: Mapped[Decimal] = mapped_column(Numeric(18, 2))
    finalized_at: Mapped[datetime] = mapped_column(DateTime(timezone=True))
    input_hash: Mapped[str] = mapped_column(String(64))
    liquidation_json: Mapped[dict] = mapped_column(JSON)


class AgentRun(Base):
    __tablename__ = "agent_runs"
    __table_args__ = (
        CheckConstraint(
            "model_id IN ('gpt-5.6-sol', 'claude-opus-4.8', 'claude-sonnet-5', "
            "'gemini-3.1-pro-preview')",
            name="ck_agent_runs_model_id",
        ),
        CheckConstraint("status IN ('OK', 'ERROR', 'MISSED')", name="ck_agent_runs_status"),
        CheckConstraint("ended_at >= started_at", name="ck_agent_runs_time_order"),
        UniqueConstraint("run_key", "attempt", name="uq_agent_runs_run_attempt"),
    )
    id: Mapped[str] = mapped_column(String(40), primary_key=True, default=uid)
    agent_id: Mapped[str] = mapped_column(ForeignKey("agents.id"), index=True)
    model_id: Mapped[str] = mapped_column(String(100))
    prompt_version: Mapped[str] = mapped_column(String(40))
    raw_response: Mapped[str] = mapped_column(String)
    sources: Mapped[list] = mapped_column(JSON)
    started_at: Mapped[datetime] = mapped_column(DateTime(timezone=True))
    ended_at: Mapped[datetime] = mapped_column(DateTime(timezone=True))
    status: Mapped[str] = mapped_column(String(20))
    run_key: Mapped[str] = mapped_column(ForeignKey("scheduled_agent_runs.run_key"), index=True)
    attempt: Mapped[int] = mapped_column(Integer)
    prompt: Mapped[str] = mapped_column(String)
    stderr: Mapped[str] = mapped_column(String)
    command_json: Mapped[list] = mapped_column(JSON)
    provenance_json: Mapped[dict] = mapped_column(JSON)
    decision_json: Mapped[dict | None] = mapped_column(JSON, nullable=True)
    validation_error: Mapped[str | None] = mapped_column(String, nullable=True)
    retry_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True), nullable=True)
    missed_reason: Mapped[str | None] = mapped_column(String(200), nullable=True)


class ScheduledAgentRun(Base):
    __tablename__ = "scheduled_agent_runs"
    __table_args__ = (
        CheckConstraint(
            "model_id IN ('gpt-5.6-sol', 'claude-opus-4.8', 'claude-sonnet-5', "
            "'gemini-3.1-pro-preview')",
            name="ck_scheduled_agent_runs_model_id",
        ),
        CheckConstraint(
            "status IN ('PENDING', 'CLAIMED', 'COMPLETED', 'MISSED')",
            name="ck_scheduled_agent_runs_status",
        ),
        CheckConstraint("deadline_at >= scheduled_at", name="ck_scheduled_agent_runs_window"),
        CheckConstraint("attempt_count >= 0", name="ck_scheduled_agent_runs_attempts"),
    )
    run_key: Mapped[str] = mapped_column(String(100), primary_key=True)
    agent_id: Mapped[str] = mapped_column(ForeignKey("agents.id"), index=True)
    model_id: Mapped[str] = mapped_column(String(100))
    scheduled_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), index=True)
    deadline_at: Mapped[datetime] = mapped_column(DateTime(timezone=True))
    status: Mapped[str] = mapped_column(String(20), default="PENDING", index=True)
    attempt_count: Mapped[int] = mapped_column(Integer, default=0)
    next_attempt_at: Mapped[datetime] = mapped_column(DateTime(timezone=True))
    claim_token: Mapped[str | None] = mapped_column(String(40), nullable=True)
    lease_until: Mapped[datetime | None] = mapped_column(DateTime(timezone=True), nullable=True)


class SystemState(Base):
    __tablename__ = "system_state"
    __table_args__ = (
        CheckConstraint("id = 1", name="ck_system_state_singleton"),
        CheckConstraint(
            "contest_status IN ('DRAFT', 'RUNNING', 'PAUSED', 'FINISHED')",
            name="ck_system_state_contest_status",
        ),
    )
    id: Mapped[int] = mapped_column(Integer, primary_key=True, default=1)
    paused: Mapped[bool] = mapped_column(Boolean, default=False)
    reason: Mapped[str | None] = mapped_column(String(200), nullable=True)
    contest_status: Mapped[str] = mapped_column(String(20), default="DRAFT")
    started_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True), nullable=True)
    finished_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True), nullable=True)


class ContestStateEvent(Base):
    __tablename__ = "contest_state_events"
    __table_args__ = (
        CheckConstraint(
            "from_status IN ('DRAFT', 'RUNNING', 'PAUSED', 'FINISHED')",
            name="ck_contest_state_events_from_status",
        ),
        CheckConstraint(
            "to_status IN ('DRAFT', 'RUNNING', 'PAUSED', 'FINISHED')",
            name="ck_contest_state_events_to_status",
        ),
        CheckConstraint(
            "trigger_type IN ('OWNER', 'BAD_DATA', 'FINALIZATION')",
            name="ck_contest_state_events_trigger",
        ),
        UniqueConstraint("idempotency_key"),
    )
    id: Mapped[str] = mapped_column(String(40), primary_key=True, default=uid)
    from_status: Mapped[str] = mapped_column(String(20))
    to_status: Mapped[str] = mapped_column(String(20))
    reason: Mapped[str] = mapped_column(String(500))
    actor_email: Mapped[str | None] = mapped_column(String(254), nullable=True)
    trigger_type: Mapped[str] = mapped_column(String(30))
    occurred_at: Mapped[datetime] = mapped_column(DateTime(timezone=True))
    idempotency_key: Mapped[str] = mapped_column(String(200))


class CriticalAlert(Base):
    __tablename__ = "critical_alerts"
    __table_args__ = (
        CheckConstraint(
            "kind IN ('SYSTEM_PAUSE', 'INVALID_MARKET_DATA', 'DATABASE_OR_BACKUP', "
            "'MULTI_MODEL_AUTH_OUTAGE', 'ACCOUNTING_INVARIANT')",
            name="ck_critical_alert_kind",
        ),
        UniqueConstraint("idempotency_key"),
    )
    id: Mapped[str] = mapped_column(String(40), primary_key=True, default=uid)
    kind: Mapped[str] = mapped_column(String(40), index=True)
    detail: Mapped[str] = mapped_column(String(1000))
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True))
    idempotency_key: Mapped[str] = mapped_column(String(200))


class DailyReport(Base):
    __tablename__ = "daily_reports"
    __table_args__ = (
        UniqueConstraint("report_key"),
        CheckConstraint("length(content_hash) = 64", name="ck_daily_reports_hash"),
    )
    id: Mapped[str] = mapped_column(String(40), primary_key=True, default=uid)
    report_key: Mapped[str] = mapped_column(String(100), index=True)
    trading_day: Mapped[str] = mapped_column(String(10), index=True)
    generated_at: Mapped[datetime] = mapped_column(DateTime(timezone=True))
    content_hash: Mapped[str] = mapped_column(String(64))
    message: Mapped[str] = mapped_column(String(6000))
    payload_json: Mapped[dict] = mapped_column(JSON)


class DeliveryAttempt(Base):
    __tablename__ = "delivery_attempts"
    __table_args__ = (
        CheckConstraint("reference_type IN ('REPORT', 'ALERT')", name="ck_delivery_reference_type"),
        CheckConstraint("status IN ('SUCCESS', 'ERROR')", name="ck_delivery_attempt_status"),
        CheckConstraint("attempt > 0", name="ck_delivery_attempt_positive"),
        UniqueConstraint("reference_type", "reference_id", "attempt"),
    )
    id: Mapped[str] = mapped_column(String(40), primary_key=True, default=uid)
    reference_type: Mapped[str] = mapped_column(String(20), index=True)
    reference_id: Mapped[str] = mapped_column(String(40), index=True)
    attempt: Mapped[int] = mapped_column(Integer)
    status: Mapped[str] = mapped_column(String(20))
    attempted_at: Mapped[datetime] = mapped_column(DateTime(timezone=True))
    error: Mapped[str | None] = mapped_column(String(1000), nullable=True)
    receipt_json: Mapped[dict | None] = mapped_column(JSON, nullable=True)


_APPEND_ONLY = (
    LedgerEvent,
    Order,
    Fill,
    OrderRejection,
    OrderLifecycleEvent,
    CorporateAction,
    FinalRanking,
    AgentRun,
    ContestStateEvent,
    CriticalAlert,
    DailyReport,
    DeliveryAttempt,
)


def _reject_audit_mutation(*_):
    raise ValueError("audit records are append-only; use a correction event")


for _model in _APPEND_ONLY:
    event.listen(_model, "before_update", _reject_audit_mutation)
    event.listen(_model, "before_delete", _reject_audit_mutation)
