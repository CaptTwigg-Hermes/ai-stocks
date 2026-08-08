import json

import pytest

from ai_stocks.delivery import DeliveryError, HermesDiscordDelivery, SeriousAlertKind


def test_uses_exact_hermes_send_argv_without_shell_or_secret(monkeypatch):
    calls = []

    def executor(argv, **kwargs):
        calls.append((argv, kwargs))
        return type(
            "Result",
            (),
            {
                "returncode": 0,
                "stdout": json.dumps({"ok": True, "platform": "discord"}),
                "stderr": "",
            },
        )()

    monkeypatch.setenv("DISCORD_REPORT_TARGET", "discord:1534963881317896212")
    sink = HermesDiscordDelivery(executor=executor)
    receipt = sink.send_report("daily report")

    assert receipt.platform == "discord"
    assert calls[0][0] == [
        "/opt/hermes/bin/hermes",
        "send",
        "--to",
        "discord:1534963881317896212",
        "--json",
        "--file",
        "-",
    ]
    assert calls[0][1]["input"] == "daily report"
    assert calls[0][1]["shell"] is False
    assert calls[0][1]["timeout"] == 30


def test_serious_alerts_are_allowlisted_and_prefixed(monkeypatch):
    messages = []

    def executor(_argv, **kwargs):
        messages.append(kwargs["input"])
        return type("Result", (), {"returncode": 0, "stdout": '{"ok":true}', "stderr": ""})()

    monkeypatch.setenv("DISCORD_REPORT_TARGET", "discord:1534963881317896212")
    sink = HermesDiscordDelivery(executor=executor)
    sink.send_alert(SeriousAlertKind.ACCOUNTING_INVARIANT, "negative holding blocked")

    assert messages == ["🚨 ACCOUNTING_INVARIANT: negative holding blocked"]


def test_fails_closed_on_bad_target_delivery_error_and_oversized_text(monkeypatch):
    monkeypatch.setenv("DISCORD_REPORT_TARGET", "https://discord.invalid/webhook")
    with pytest.raises(DeliveryError):
        HermesDiscordDelivery(executor=lambda *_a, **_kw: None)

    monkeypatch.setenv("DISCORD_REPORT_TARGET", "discord:1534963881317896212")

    def failed(_argv, **_kwargs):
        return type("Result", (), {"returncode": 1, "stdout": "", "stderr": "token=secret"})()

    sink = HermesDiscordDelivery(executor=failed)
    with pytest.raises(DeliveryError, match="delivery failed") as exc:
        sink.send_report("report")
    assert "secret" not in str(exc.value)

    with pytest.raises(DeliveryError, match="length"):
        sink.send_report("x" * 6001)
