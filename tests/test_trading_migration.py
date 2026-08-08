import os
import sqlite3
import uuid
from pathlib import Path

import pytest
from alembic.config import Config
from sqlalchemy import create_engine, inspect, text

from alembic import command


def alembic_config(database_url):
    root = Path(__file__).parents[1]
    config = Config(str(root / "alembic.ini"))
    config.set_main_option("script_location", str(root / "alembic"))
    config.set_main_option("sqlalchemy.url", database_url)
    return config


def test_sqlite_migration_upgrade_constraints_append_only_and_downgrade(tmp_path):
    database = tmp_path / "migration.db"
    url = f"sqlite:///{database}"
    config = alembic_config(url)
    command.upgrade(config, "head")
    engine = create_engine(url)
    assert {"agents", "orders", "fills", "ledger_events", "order_rejections", "agent_runs"} <= set(
        inspect(engine).get_table_names()
    )
    with engine.begin() as connection:
        connection.execute(
            text(
                "INSERT INTO agents (id,model_id,initial_cash,fee_tier,stock_trade_count) VALUES ('a0','gpt-5.6-sol',30000,'STARTER',0)"
            )
        )
        connection.execute(
            text(
                "INSERT INTO ledger_events (id,agent_id,event_type,cash_delta,quantity_delta,occurred_at,reference_id,metadata_json) "
                "VALUES ('l1','a0','INITIAL_CASH',30000,0,'2026-08-05','initial:a0','{}')"
            )
        )
        connection.execute(
            text(
                "INSERT INTO agent_runs (id,agent_id,model_id,prompt_version,raw_response,sources,started_at,ended_at,status) VALUES ('r1','a0','gpt-5.6-sol','1','{}','[]','2026-08-06','2026-08-06','OK')"
            )
        )
        connection.execute(
            text(
                "INSERT INTO agents (id,model_id,initial_cash,fee_tier,stock_trade_count) VALUES ('a1','claude-opus-4.8',30000,'STARTER',0)"
            )
        )
        connection.execute(
            text(
                "INSERT INTO ledger_events (id,agent_id,event_type,cash_delta,quantity_delta,occurred_at,reference_id,metadata_json) VALUES ('a1cash','a1','INITIAL_CASH',30000,0,'2026-08-05','initial:a1','{}')"
            )
        )
        connection.execute(
            text(
                "INSERT INTO orders (id,decision_id,request_hash,agent_id,symbol,side,quantity,status,decision_at,created_at,evidence_json,quote_json,fill_price,fee) VALUES ('o1','d1','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','a0','VOLV-B','BUY',1,'FILLED','2026-08-06','2026-08-06','{}','{}',100,0)"
            )
        )
        connection.execute(
            text(
                "INSERT INTO ledger_events (id,agent_id,event_type,symbol,cash_delta,quantity_delta,occurred_at,reference_id,order_id,metadata_json) VALUES ('fill-ledger','a0','FILL','VOLV-B',-100,1,'2026-08-06','fill:d1','o1','{}')"
            )
        )
    for statement in (
        "INSERT INTO ledger_events (id,agent_id,event_type,cash_delta,quantity_delta,occurred_at,reference_id,metadata_json) VALUES ('badcash','a0','FILL',-30001,0,'2026-08-06','badcash','{}')",
        "INSERT INTO ledger_events (id,agent_id,event_type,cash_delta,quantity_delta,symbol,occurred_at,reference_id,metadata_json) VALUES ('badshares','a0','FILL',0,-2,'VOLV-B','2026-08-06','badshares','{}')",
    ):
        with pytest.raises(Exception, match="nonnegative"):
            with engine.begin() as connection:
                connection.execute(text(statement))
    for statement in (
        "INSERT INTO agents (id,model_id,initial_cash,fee_tier,stock_trade_count) VALUES ('badmodel','bad',30000,'STARTER',0)",
        "INSERT INTO system_state (id,paused) VALUES (2,0)",
        "INSERT INTO agent_runs (id,agent_id,model_id,prompt_version,raw_response,sources,started_at,ended_at,status) VALUES ('bad-run','a0','gpt-5.6-sol','1','{}','[]','2026-08-07','2026-08-06','OK')",
        "INSERT INTO agent_runs (id,agent_id,model_id,prompt_version,raw_response,sources,started_at,ended_at,status) VALUES ('wrong-model','a0','claude-opus-4.8','1','{}','[]','2026-08-06','2026-08-06','OK')",
        "INSERT INTO fills (id,order_id,ledger_event_id,agent_id,executed_at,fill_price,gross,fee,quote_json,created_at) VALUES ('bad-fill','o1','fill-ledger','a1','2026-08-06',100,100,0,'{}','2026-08-06')",
    ):
        with pytest.raises(Exception):
            with engine.begin() as connection:
                connection.execute(text(statement))
    with pytest.raises(Exception, match="append-only"):
        with engine.begin() as connection:
            connection.execute(text("UPDATE agent_runs SET status='BAD' WHERE id='r1'"))
    with pytest.raises(Exception):
        with engine.begin() as connection:
            connection.execute(text("UPDATE agents SET fee_tier='BROKEN' WHERE id='a0'"))
    engine.dispose()
    command.downgrade(config, "base")
    connection = sqlite3.connect(database)
    assert not {"agents", "orders", "fills"} & {
        row[0] for row in connection.execute("SELECT name FROM sqlite_master WHERE type='table'")
    }
    connection.close()


@pytest.mark.skipif(not os.getenv("TEST_POSTGRES_URL"), reason="TEST_POSTGRES_URL required")
def test_postgresql_schema_can_be_created_with_constraints_and_triggers():
    from ai_stocks.db import Base

    url = os.environ["TEST_POSTGRES_URL"]
    schema = "ai_stocks_test_" + uuid.uuid4().hex
    admin = create_engine(url)
    with admin.begin() as connection:
        connection.execute(text(f'CREATE SCHEMA "{schema}"'))
    engine = create_engine(url, connect_args={"options": f"-csearch_path={schema}"})
    try:
        Base.metadata.create_all(engine)
        assert "orders" in inspect(engine).get_table_names()
        with pytest.raises(Exception):
            with engine.begin() as connection:
                connection.execute(
                    text(
                        "INSERT INTO agents (id,model_id,initial_cash,fee_tier,stock_trade_count) VALUES ('bad','bad',1,'BROKEN',-1)"
                    )
                )
    finally:
        engine.dispose()
        with admin.begin() as connection:
            connection.execute(text(f'DROP SCHEMA "{schema}" CASCADE'))
        admin.dispose()
