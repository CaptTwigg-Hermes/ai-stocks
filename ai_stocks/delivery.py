"""Discord delivery through the already-configured Hermes gateway credentials."""

from __future__ import annotations

import json
import os
import re

# Fixed executable and argv; shell is always disabled.
import subprocess  # nosec B404
from collections.abc import Callable
from dataclasses import dataclass
from enum import StrEnum
from typing import Any

_HERMES = "/opt/hermes/bin/hermes"
_TARGET_RE = re.compile(r"discord:\d+(?::\d+)?\Z")
_MAX_MESSAGE = 6000


class DeliveryError(RuntimeError):
    pass


class SeriousAlertKind(StrEnum):
    SYSTEM_PAUSE = "SYSTEM_PAUSE"
    INVALID_MARKET_DATA = "INVALID_MARKET_DATA"
    DATABASE_OR_BACKUP = "DATABASE_OR_BACKUP"
    MULTI_MODEL_AUTH_OUTAGE = "MULTI_MODEL_AUTH_OUTAGE"
    ACCOUNTING_INVARIANT = "ACCOUNTING_INVARIANT"


@dataclass(frozen=True)
class DeliveryReceipt:
    platform: str
    response: dict[str, Any]


class HermesDiscordDelivery:
    def __init__(
        self,
        *,
        executor: Callable[..., Any] = subprocess.run,
        target: str | None = None,
    ) -> None:
        self.executor = executor
        self.target = target or os.getenv("DISCORD_REPORT_TARGET", "")
        if not _TARGET_RE.fullmatch(self.target):
            raise DeliveryError("DISCORD_REPORT_TARGET must be a numeric Discord channel target")

    @staticmethod
    def _validate_message(message: str) -> None:
        if not isinstance(message, str) or not message.strip() or len(message) > _MAX_MESSAGE:
            raise DeliveryError("Discord message length is outside the allowed range")

    def send_report(self, message: str) -> DeliveryReceipt:
        self._validate_message(message)
        argv = [
            _HERMES,
            "send",
            "--to",
            self.target,
            "--json",
            "--file",
            "-",
        ]
        try:
            result = self.executor(
                argv,
                input=message,
                capture_output=True,
                text=True,
                shell=False,
                timeout=30,
                check=False,
            )
        except (OSError, subprocess.SubprocessError) as exc:
            raise DeliveryError("Discord delivery failed") from exc
        if result.returncode != 0:
            raise DeliveryError("Discord delivery failed")
        if len(result.stdout) > 64_000:
            raise DeliveryError("Discord delivery returned an oversized receipt")
        try:
            payload = json.loads(result.stdout)
        except (TypeError, ValueError) as exc:
            raise DeliveryError("Discord delivery returned an invalid receipt") from exc
        if not isinstance(payload, dict) or payload.get("ok") is not True:
            raise DeliveryError("Discord delivery did not confirm success")
        return DeliveryReceipt(platform=str(payload.get("platform", "discord")), response=payload)

    def send_alert(self, kind: SeriousAlertKind, detail: str) -> DeliveryReceipt:
        if not isinstance(kind, SeriousAlertKind):
            raise DeliveryError("unsupported immediate-alert kind")
        if not isinstance(detail, str) or not detail.strip() or len(detail) > 1000:
            raise DeliveryError("alert detail length is outside the allowed range")
        return self.send_report(f"🚨 {kind.value}: {detail}")
