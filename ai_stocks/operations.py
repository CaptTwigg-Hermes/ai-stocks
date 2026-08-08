"""Durable contest controls, alerts, and deterministic Discord reports."""

from __future__ import annotations

import hashlib
import json
from datetime import datetime, time
from decimal import Decimal, InvalidOperation

from sqlalchemy import func, select, text
from sqlalchemy.orm import Session

from .auth import AccessIdentity
from .calendar import STOCKHOLM, session_for
from .delivery import HermesDiscordDelivery, SeriousAlertKind
from .models import (
    ContestStateEvent,
    CriticalAlert,
    DailyReport,
    DeliveryAttempt,
    FinalRanking,
    SystemState,
)
from .schedule import AGENT_BY_MODEL


class ContestOperationError(ValueError):
    pass


class ContestOperations:
    def __init__(self, session: Session):
        self.session = session

    def start(self, identity: AccessIdentity, *, at: datetime, idempotency_key: str):
        return self._owner_transition(
            identity, "DRAFT", "RUNNING", "owner start", at, idempotency_key
        )

    def pause(
        self,
        identity: AccessIdentity,
        *,
        reason: str,
        at: datetime,
        idempotency_key: str,
    ):
        return self._owner_transition(identity, "RUNNING", "PAUSED", reason, at, idempotency_key)

    def resume(self, identity: AccessIdentity, *, at: datetime, idempotency_key: str):
        return self._owner_transition(
            identity, "PAUSED", "RUNNING", "owner resume", at, idempotency_key
        )

    def prestart_reset(
        self,
        identity: AccessIdentity,
        *,
        reason: str,
        at: datetime,
        idempotency_key: str,
    ):
        return self._owner_transition(identity, "DRAFT", "DRAFT", reason, at, idempotency_key)

    def finish(
        self,
        identity: AccessIdentity,
        *,
        ranking_reference: str,
        at: datetime,
        idempotency_key: str,
    ):
        self._require_owner(identity)
        ranking = self.session.scalars(
            select(FinalRanking)
            .where(FinalRanking.reference == ranking_reference)
            .order_by(FinalRanking.rank)
        ).all()
        if len(ranking) != 4 or [row.rank for row in ranking] != [1, 2, 3, 4]:
            raise ContestOperationError("complete immutable final ranking required")
        return self._transition(
            expected=("RUNNING", "PAUSED"),
            target="FINISHED",
            reason=f"final ranking {ranking_reference}",
            actor_email=identity.email,
            trigger_type="FINALIZATION",
            at=at,
            idempotency_key=idempotency_key,
        )

    def pause_for_bad_data(
        self, *, reason: str, at: datetime, idempotency_key: str
    ) -> CriticalAlert:
        self._validate_text(reason, 500, "pause reason")
        self._validate_key(idempotency_key)
        self._require_aware(at)
        alert_key = f"bad-data:{idempotency_key}"
        existing = self.session.scalar(
            select(CriticalAlert).where(CriticalAlert.idempotency_key == alert_key)
        )
        if existing:
            if existing.detail != reason or existing.kind != SeriousAlertKind.INVALID_MARKET_DATA:
                raise ContestOperationError("bad-data idempotency conflict")
            return existing
        self._transition(
            expected=("RUNNING",),
            target="PAUSED",
            reason=reason,
            actor_email=None,
            trigger_type="BAD_DATA",
            at=at,
            idempotency_key=idempotency_key,
            commit=False,
        )
        alert = CriticalAlert(
            kind=SeriousAlertKind.INVALID_MARKET_DATA,
            detail=reason,
            created_at=at,
            idempotency_key=alert_key,
        )
        self.session.add(alert)
        self.session.commit()
        return alert

    def record_alert(
        self, kind: SeriousAlertKind, detail: str, *, at: datetime, idempotency_key: str
    ) -> CriticalAlert:
        if not isinstance(kind, SeriousAlertKind):
            raise ContestOperationError("unsupported critical alert kind")
        self._validate_text(detail, 1000, "alert detail")
        self._validate_key(idempotency_key)
        self._require_aware(at)
        existing = self.session.scalar(
            select(CriticalAlert).where(CriticalAlert.idempotency_key == idempotency_key)
        )
        if existing:
            if existing.kind != kind or existing.detail != detail:
                raise ContestOperationError("alert idempotency conflict")
            return existing
        alert = CriticalAlert(
            kind=kind, detail=detail, created_at=at, idempotency_key=idempotency_key
        )
        self.session.add(alert)
        self.session.commit()
        return alert

    def create_daily_report(self, trading_day, snapshots: list[dict], *, generated_at: datetime):
        self._require_aware(generated_at)
        local = generated_at.astimezone(STOCKHOLM)
        if (
            local.date() != trading_day
            or local.time().replace(second=0, microsecond=0) != time(18, 30)
            or session_for(trading_day) is None
        ):
            raise ContestOperationError("daily report must be generated at 18:30 Stockholm")
        rows = self._normalize_snapshots(snapshots)
        payload = {"schema_version": 1, "trading_day": trading_day.isoformat(), "agents": rows}
        canonical = json.dumps(payload, sort_keys=True, separators=(",", ":"))
        digest = hashlib.sha256(canonical.encode()).hexdigest()
        report_key = f"daily:{trading_day.isoformat()}"
        existing = self.session.scalar(
            select(DailyReport).where(DailyReport.report_key == report_key)
        )
        if existing:
            if existing.content_hash != digest:
                raise ContestOperationError("daily report idempotency conflict")
            return existing
        message = self._render_report(trading_day.isoformat(), rows)
        report = DailyReport(
            report_key=report_key,
            trading_day=trading_day.isoformat(),
            generated_at=generated_at,
            content_hash=digest,
            message=message,
            payload_json=payload,
        )
        self.session.add(report)
        self.session.commit()
        return report

    def deliver_report(
        self, report_id: str, delivery: HermesDiscordDelivery, *, attempted_at: datetime
    ) -> DeliveryAttempt:
        report = self._locked(DailyReport, report_id)
        if report is None:
            raise ContestOperationError("daily report not found")
        return self._deliver(
            "REPORT", report.id, attempted_at, lambda: delivery.send_report(report.message)
        )

    def deliver_alert(
        self, alert_id: str, delivery: HermesDiscordDelivery, *, attempted_at: datetime
    ) -> DeliveryAttempt:
        alert = self._locked(CriticalAlert, alert_id)
        if alert is None:
            raise ContestOperationError("critical alert not found")
        kind = SeriousAlertKind(alert.kind)
        return self._deliver(
            "ALERT", alert.id, attempted_at, lambda: delivery.send_alert(kind, alert.detail)
        )

    def _deliver(self, reference_type, reference_id, attempted_at, sender):
        self._require_aware(attempted_at)
        prior = self.session.scalar(
            select(DeliveryAttempt).where(
                DeliveryAttempt.reference_type == reference_type,
                DeliveryAttempt.reference_id == reference_id,
                DeliveryAttempt.status == "SUCCESS",
            )
        )
        if prior:
            self.session.commit()
            return prior
        attempt = (
            self.session.scalar(
                select(func.max(DeliveryAttempt.attempt)).where(
                    DeliveryAttempt.reference_type == reference_type,
                    DeliveryAttempt.reference_id == reference_id,
                )
            )
            or 0
        ) + 1
        try:
            receipt = sender()
        except Exception as exc:
            row = DeliveryAttempt(
                reference_type=reference_type,
                reference_id=reference_id,
                attempt=attempt,
                status="ERROR",
                attempted_at=attempted_at,
                error=f"{type(exc).__name__}: {exc}"[:1000],
            )
            self.session.add(row)
            self.session.commit()
            raise ContestOperationError("Discord delivery failed") from exc
        row = DeliveryAttempt(
            reference_type=reference_type,
            reference_id=reference_id,
            attempt=attempt,
            status="SUCCESS",
            attempted_at=attempted_at,
            receipt_json={"platform": receipt.platform, "response": receipt.response},
        )
        self.session.add(row)
        self.session.commit()
        return row

    def _owner_transition(self, identity, expected, target, reason, at, idempotency_key):
        self._require_owner(identity)
        return self._transition(
            expected=(expected,),
            target=target,
            reason=reason,
            actor_email=identity.email,
            trigger_type="OWNER",
            at=at,
            idempotency_key=idempotency_key,
        )

    def _transition(
        self,
        *,
        expected,
        target,
        reason,
        actor_email,
        trigger_type,
        at,
        idempotency_key,
        commit=True,
    ):
        self._validate_text(reason, 500, "transition reason")
        self._validate_key(idempotency_key)
        self._require_aware(at)
        if self.session.bind and self.session.bind.dialect.name == "postgresql":
            self.session.execute(text("SELECT pg_advisory_xact_lock(420042)"))
        existing = self.session.scalar(
            select(ContestStateEvent).where(ContestStateEvent.idempotency_key == idempotency_key)
        )
        if existing:
            if (
                existing.to_status != target
                or existing.reason != reason
                or existing.actor_email != actor_email
                or existing.trigger_type != trigger_type
            ):
                raise ContestOperationError("contest transition idempotency conflict")
            return self._locked_state()
        state = self._locked_state()
        if state.contest_status not in expected:
            raise ContestOperationError(
                f"contest cannot transition from {state.contest_status} to {target}"
            )
        previous = state.contest_status
        state.contest_status = target
        state.paused = target == "PAUSED"
        state.reason = reason if state.paused else None
        if target == "RUNNING" and previous == "DRAFT":
            state.started_at = at
        if target == "FINISHED":
            state.finished_at = at
        self.session.add(
            ContestStateEvent(
                from_status=previous,
                to_status=target,
                reason=reason,
                actor_email=actor_email,
                trigger_type=trigger_type,
                occurred_at=at,
                idempotency_key=idempotency_key,
            )
        )
        if commit:
            self.session.commit()
        else:
            self.session.flush()
        return state

    def _locked_state(self):
        state = self._locked(SystemState, 1)
        if state is None:
            state = SystemState(id=1, contest_status="DRAFT", paused=False)
            self.session.add(state)
            self.session.flush()
        return state

    def _locked(self, model, identity):
        statement = select(model).where(model.id == identity)
        if self.session.bind and self.session.bind.dialect.name == "postgresql":
            statement = statement.with_for_update()
        return self.session.scalar(statement)

    @staticmethod
    def _normalize_snapshots(snapshots):
        expected = set(AGENT_BY_MODEL.values())
        if len(snapshots) != 4 or {row.get("agent_id") for row in snapshots} != expected:
            raise ContestOperationError("daily report requires all four isolated agents")
        rows = []
        model_by_agent = {agent: model for model, agent in AGENT_BY_MODEL.items()}
        for item in snapshots:
            agent_id = item["agent_id"]
            if item.get("model_id") != model_by_agent[agent_id]:
                raise ContestOperationError("daily report model identity mismatch")
            net = ContestOperations._decimal(item.get("net_value"), "net value")
            initial = Decimal("30000")
            return_pct = ((net - initial) / initial * Decimal(100)).quantize(Decimal("0.01"))
            trades = item.get("trades")
            holdings = item.get("holdings")
            if type(trades) is not int or trades < 0 or not isinstance(holdings, dict):
                raise ContestOperationError("daily report snapshot is invalid")
            rows.append(
                {
                    "agent_id": agent_id,
                    "model_id": item["model_id"],
                    "net_value": f"{net:.2f}",
                    "return_pct": f"{return_pct:.2f}",
                    "cash": f"{ContestOperations._decimal(item.get('cash'), 'cash'):.2f}",
                    "holdings": {key: holdings[key] for key in sorted(holdings)},
                    "trades": trades,
                }
            )
        rows.sort(key=lambda row: (-Decimal(row["net_value"]), row["model_id"]))
        for rank, row in enumerate(rows, 1):
            row["rank"] = rank
        return rows

    @staticmethod
    def _render_report(day, rows):
        lines = [f"AI Stocks — {day} — 18:30 Stockholm"]
        for row in rows:
            lines.append(
                f"{row['rank']}. {row['model_id']}: {row['net_value']} SEK "
                f"({row['return_pct']}%); cash {row['cash']}; trades {row['trades']}; "
                f"holdings {json.dumps(row['holdings'], sort_keys=True, separators=(',', ':'))}"
            )
        return "\n".join(lines)

    @staticmethod
    def _decimal(value, label):
        try:
            number = Decimal(value)
        except (InvalidOperation, TypeError, ValueError) as exc:
            raise ContestOperationError(f"daily report {label} is invalid") from exc
        if not number.is_finite() or number < 0:
            raise ContestOperationError(f"daily report {label} is invalid")
        return number.quantize(Decimal("0.01"))

    @staticmethod
    def _require_owner(identity):
        if not isinstance(identity, AccessIdentity) or identity.role != "owner":
            raise ContestOperationError("verified owner identity required")

    @staticmethod
    def _require_aware(at):
        if not isinstance(at, datetime) or at.tzinfo is None or at.utcoffset() is None:
            raise ContestOperationError("timestamp must be timezone-aware")

    @staticmethod
    def _validate_text(value, maximum, label):
        if not isinstance(value, str) or not value.strip() or len(value) > maximum:
            raise ContestOperationError(f"{label} is invalid")

    @staticmethod
    def _validate_key(value):
        if not isinstance(value, str) or not value.strip() or len(value) > 200:
            raise ContestOperationError("idempotency key is invalid")
