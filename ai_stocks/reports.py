from datetime import date


def trading_day_report(day: date, agents: list[dict]) -> str:
    lines = [f"AI Stocks — {day.isoformat()}"]
    for a in agents:
        lines.append(
            f"{a['model_id']}: cash {a['cash']} SEK; holdings {a['holdings']}; runs {a['runs']}; missed {a.get('missed', 0)}"
        )
    return "\n".join(lines)


class AlertSink:
    def send(self, kind: str, message: str):
        raise NotImplementedError


class FakeAlertSink(AlertSink):
    def __init__(self):
        self.alerts = []

    def send(self, kind, message):
        if kind not in {
            "SYSTEM_PAUSE",
            "RUN_WIDE_MARKET_DATA",
            "DB_BACKUP_FAILURE",
            "MULTI_MODEL_AUTH_OUTAGE",
            "ACCOUNTING_INVARIANT",
        }:
            raise ValueError("non-immediate alert type")
        self.alerts.append((kind, message))
