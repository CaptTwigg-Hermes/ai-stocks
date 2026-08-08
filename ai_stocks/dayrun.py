from datetime import date
from decimal import Decimal

from .app import MODELS
from .reports import trading_day_report


def run_deterministic_day():
    agents = [
        {"id": f"a{i}", "model_id": m, "cash": "30000.00", "holdings": {}, "runs": 6, "missed": 0}
        for i, m in enumerate(MODELS)
    ]
    return {
        "date": "2026-08-06",
        "agents": agents,
        "runs": 24,
        "ledger_balanced": sum(Decimal(a["cash"]) for a in agents) == Decimal("120000.00"),
        "report": trading_day_report(date(2026, 8, 6), agents),
    }
