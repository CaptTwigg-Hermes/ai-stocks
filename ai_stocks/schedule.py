"""Deterministic session-relative run windows for the four competitors."""

from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime, timedelta
from enum import StrEnum

from .calendar import TradingSession, six_run_times

FIXED_MODELS = (
    "gpt-5.6-sol",
    "claude-opus-4.8",
    "claude-sonnet-5",
    "gemini-3.1-pro-preview",
)

AGENT_BY_MODEL = {model: f"a{index}" for index, model in enumerate(FIXED_MODELS)}
_RETRY_WINDOW = timedelta(minutes=15)


class RunWindowState(StrEnum):
    PENDING = "PENDING"
    RUNNABLE = "RUNNABLE"
    EXPIRED = "EXPIRED"


@dataclass(frozen=True)
class RunWindow:
    id: str
    agent_id: str
    model_id: str
    sequence: int
    scheduled_at: datetime
    deadline_at: datetime

    def state_at(self, now: datetime) -> RunWindowState:
        if now.tzinfo is None:
            raise ValueError("run-window clock must be timezone-aware")
        if now < self.scheduled_at:
            return RunWindowState.PENDING
        if now <= self.deadline_at:
            return RunWindowState.RUNNABLE
        return RunWindowState.EXPIRED


def build_run_windows(session: TradingSession) -> tuple[RunWindow, ...]:
    windows = []
    for model_id in FIXED_MODELS:
        agent_id = AGENT_BY_MODEL[model_id]
        for sequence, scheduled_at in enumerate(six_run_times(session), 1):
            windows.append(
                RunWindow(
                    id=f"{session.day.isoformat()}:{agent_id}:{sequence}",
                    agent_id=agent_id,
                    model_id=model_id,
                    sequence=sequence,
                    scheduled_at=scheduled_at,
                    deadline_at=scheduled_at + _RETRY_WINDOW,
                )
            )
    return tuple(sorted(windows, key=lambda item: (item.scheduled_at, item.agent_id)))
