"""initial immutable contest schema
Revision ID: ab3aa6da9692
Revises:
"""

from collections.abc import Sequence

import sqlalchemy as sa

from alembic import op

revision: str = "ab3aa6da9692"
down_revision: str | Sequence[str] | None = None
branch_labels = None
depends_on = None

AUDIT_TABLES = ("orders", "ledger_events", "fills", "order_rejections", "agent_runs")


def upgrade():
    op.create_table(
        "agents",
        sa.Column("id", sa.String(length=40), nullable=False),
        sa.Column("model_id", sa.String(length=100), nullable=False),
        sa.Column("initial_cash", sa.Numeric(18, 2), nullable=False),
        sa.Column("fee_tier", sa.String(length=20), nullable=False),
        sa.Column("stock_trade_count", sa.Integer(), nullable=False),
        sa.CheckConstraint("initial_cash = 30000.00", name="ck_agents_initial_cash"),
        sa.CheckConstraint(
            "model_id IN ('gpt-5.6-sol', 'claude-opus-4.8', 'claude-sonnet-5', "
            "'gemini-3.1-pro-preview')",
            name="ck_agents_model_id",
        ),
        sa.CheckConstraint("fee_tier IN ('STARTER', 'MINI')", name="ck_agents_fee_tier"),
        sa.CheckConstraint("stock_trade_count >= 0", name="ck_agents_trade_count"),
        sa.PrimaryKeyConstraint("id"),
        sa.UniqueConstraint("model_id"),
    )
    op.create_table(
        "system_state",
        sa.Column("id", sa.Integer(), nullable=False),
        sa.Column("paused", sa.Boolean(), nullable=False),
        sa.Column("reason", sa.String(length=200), nullable=True),
        sa.CheckConstraint("id = 1", name="ck_system_state_singleton"),
        sa.PrimaryKeyConstraint("id"),
    )
    op.create_table(
        "agent_runs",
        sa.Column("id", sa.String(length=40), nullable=False),
        sa.Column("agent_id", sa.String(length=40), nullable=False),
        sa.Column("model_id", sa.String(length=100), nullable=False),
        sa.Column("prompt_version", sa.String(length=40), nullable=False),
        sa.Column("raw_response", sa.String(), nullable=False),
        sa.Column("sources", sa.JSON(), nullable=False),
        sa.Column("started_at", sa.DateTime(timezone=True), nullable=False),
        sa.Column("ended_at", sa.DateTime(timezone=True), nullable=False),
        sa.Column("status", sa.String(length=20), nullable=False),
        sa.CheckConstraint(
            "model_id IN ('gpt-5.6-sol', 'claude-opus-4.8', 'claude-sonnet-5', "
            "'gemini-3.1-pro-preview')",
            name="ck_agent_runs_model_id",
        ),
        sa.CheckConstraint("status IN ('OK', 'ERROR', 'MISSED')", name="ck_agent_runs_status"),
        sa.CheckConstraint("ended_at >= started_at", name="ck_agent_runs_time_order"),
        sa.ForeignKeyConstraint(["agent_id"], ["agents.id"]),
        sa.PrimaryKeyConstraint("id"),
    )
    op.create_index("ix_agent_runs_agent_id", "agent_runs", ["agent_id"])
    op.create_table(
        "orders",
        sa.Column("id", sa.String(length=40), nullable=False),
        sa.Column("decision_id", sa.String(length=100), nullable=False),
        sa.Column("request_hash", sa.String(length=64), nullable=False),
        sa.Column("agent_id", sa.String(length=40), nullable=False),
        sa.Column("symbol", sa.String(length=30), nullable=False),
        sa.Column("side", sa.String(length=4), nullable=False),
        sa.Column("quantity", sa.Integer(), nullable=False),
        sa.Column("status", sa.String(length=20), nullable=False),
        sa.Column("decision_at", sa.DateTime(timezone=True), nullable=False),
        sa.Column("created_at", sa.DateTime(timezone=True), nullable=False),
        sa.Column("evidence_json", sa.JSON(), nullable=False),
        sa.Column("quote_json", sa.JSON(), nullable=True),
        sa.Column("fill_price", sa.Numeric(18, 4), nullable=True),
        sa.Column("fee", sa.Numeric(18, 2), nullable=True),
        sa.Column("rejection_reason", sa.String(length=200), nullable=True),
        sa.CheckConstraint("side IN ('BUY', 'SELL')", name="ck_orders_side"),
        sa.CheckConstraint("quantity > 0", name="ck_orders_quantity"),
        sa.CheckConstraint(
            "status IN ('QUEUED', 'FILLED', 'REJECTED', 'CANCELLED')",
            name="ck_orders_status",
        ),
        sa.CheckConstraint("length(request_hash) = 64", name="ck_orders_request_hash"),
        sa.ForeignKeyConstraint(["agent_id"], ["agents.id"]),
        sa.PrimaryKeyConstraint("id"),
        sa.UniqueConstraint("decision_id"),
    )
    op.create_index("ix_orders_agent_id", "orders", ["agent_id"])
    op.create_table(
        "ledger_events",
        sa.Column("id", sa.String(length=40), nullable=False),
        sa.Column("agent_id", sa.String(length=40), nullable=False),
        sa.Column("event_type", sa.String(length=40), nullable=False),
        sa.Column("symbol", sa.String(length=30), nullable=True),
        sa.Column("cash_delta", sa.Numeric(18, 2), nullable=False),
        sa.Column("quantity_delta", sa.Integer(), nullable=False),
        sa.Column("occurred_at", sa.DateTime(timezone=True), nullable=False),
        sa.Column("reference_id", sa.String(length=100), nullable=True),
        sa.Column("order_id", sa.String(length=40), nullable=True),
        sa.Column("metadata_json", sa.JSON(), nullable=False),
        sa.ForeignKeyConstraint(["agent_id"], ["agents.id"]),
        sa.ForeignKeyConstraint(["order_id"], ["orders.id"], deferrable=True, initially="DEFERRED"),
        sa.PrimaryKeyConstraint("id"),
        sa.UniqueConstraint("agent_id", "reference_id"),
    )
    op.create_index("ix_ledger_events_agent_id", "ledger_events", ["agent_id"])
    op.create_index("ix_ledger_events_event_type", "ledger_events", ["event_type"])
    op.create_index("ix_ledger_events_order_id", "ledger_events", ["order_id"])
    op.create_table(
        "fills",
        sa.Column("id", sa.String(length=40), nullable=False),
        sa.Column("order_id", sa.String(length=40), nullable=False),
        sa.Column("ledger_event_id", sa.String(length=40), nullable=False),
        sa.Column("agent_id", sa.String(length=40), nullable=False),
        sa.Column("executed_at", sa.DateTime(timezone=True), nullable=False),
        sa.Column("fill_price", sa.Numeric(18, 4), nullable=False),
        sa.Column("gross", sa.Numeric(18, 2), nullable=False),
        sa.Column("fee", sa.Numeric(18, 2), nullable=False),
        sa.Column("quote_json", sa.JSON(), nullable=False),
        sa.Column("created_at", sa.DateTime(timezone=True), nullable=False),
        sa.CheckConstraint("fill_price > 0", name="ck_fills_price"),
        sa.CheckConstraint("gross > 0", name="ck_fills_gross"),
        sa.CheckConstraint("fee >= 0", name="ck_fills_fee"),
        sa.ForeignKeyConstraint(["agent_id"], ["agents.id"]),
        sa.ForeignKeyConstraint(["ledger_event_id"], ["ledger_events.id"]),
        sa.ForeignKeyConstraint(["order_id"], ["orders.id"]),
        sa.PrimaryKeyConstraint("id"),
        sa.UniqueConstraint("ledger_event_id"),
        sa.UniqueConstraint("order_id"),
    )
    op.create_index("ix_fills_agent_id", "fills", ["agent_id"])
    op.create_index("ix_fills_order_id", "fills", ["order_id"])
    op.create_table(
        "order_rejections",
        sa.Column("id", sa.String(length=40), nullable=False),
        sa.Column("order_id", sa.String(length=40), nullable=False),
        sa.Column("decision_id", sa.String(length=100), nullable=False),
        sa.Column("attempted_request_hash", sa.String(length=64), nullable=False),
        sa.Column("attempted_request_json", sa.JSON(), nullable=False),
        sa.Column("reason", sa.String(length=200), nullable=False),
        sa.Column("rejected_at", sa.DateTime(timezone=True), nullable=False),
        sa.ForeignKeyConstraint(["order_id"], ["orders.id"]),
        sa.PrimaryKeyConstraint("id"),
        sa.UniqueConstraint("order_id", "attempted_request_hash", "reason"),
    )
    op.create_index("ix_order_rejections_order_id", "order_rejections", ["order_id"])
    op.create_index("ix_order_rejections_decision_id", "order_rejections", ["decision_id"])

    bind = op.get_bind()
    if bind.dialect.name == "postgresql":
        op.execute(
            """CREATE FUNCTION enforce_ledger_nonnegative() RETURNS trigger LANGUAGE plpgsql AS $$
            DECLARE cash_total numeric; holding_total bigint;
            BEGIN
                PERFORM 1 FROM agents WHERE id = NEW.agent_id FOR UPDATE;
                SELECT COALESCE(SUM(cash_delta), 0) + NEW.cash_delta
                  INTO cash_total FROM ledger_events WHERE agent_id = NEW.agent_id;
                IF cash_total < 0 THEN
                    RAISE EXCEPTION 'cash balance must remain nonnegative';
                END IF;
                IF NEW.symbol IS NOT NULL THEN
                    SELECT COALESCE(SUM(quantity_delta), 0) + NEW.quantity_delta
                      INTO holding_total FROM ledger_events
                     WHERE agent_id = NEW.agent_id AND symbol = NEW.symbol;
                    IF holding_total < 0 THEN
                        RAISE EXCEPTION 'holding balance must remain nonnegative';
                    END IF;
                END IF;
                RETURN NEW;
            END $$"""
        )
        op.execute(
            "CREATE TRIGGER ledger_events_nonnegative BEFORE INSERT ON ledger_events "
            "FOR EACH ROW EXECUTE FUNCTION enforce_ledger_nonnegative()"
        )
        op.execute(
            """CREATE FUNCTION enforce_audit_identity() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF TG_TABLE_NAME = 'agent_runs' AND NOT EXISTS (
                    SELECT 1 FROM agents WHERE id = NEW.agent_id AND model_id = NEW.model_id
                ) THEN
                    RAISE EXCEPTION 'agent run identity mismatch';
                END IF;
                IF TG_TABLE_NAME = 'fills' AND NOT EXISTS (
                    SELECT 1 FROM orders o JOIN ledger_events l ON l.id = NEW.ledger_event_id
                     WHERE o.id = NEW.order_id AND o.agent_id = NEW.agent_id
                       AND l.agent_id = NEW.agent_id AND l.order_id = NEW.order_id
                       AND l.event_type = 'FILL'
                ) THEN
                    RAISE EXCEPTION 'fill accounting identity mismatch';
                END IF;
                RETURN NEW;
            END $$"""
        )
        op.execute(
            "CREATE TRIGGER agent_runs_identity BEFORE INSERT ON agent_runs "
            "FOR EACH ROW EXECUTE FUNCTION enforce_audit_identity()"
        )
        op.execute(
            "CREATE TRIGGER fills_identity BEFORE INSERT ON fills "
            "FOR EACH ROW EXECUTE FUNCTION enforce_audit_identity()"
        )
        op.execute(
            """CREATE FUNCTION reject_audit_mutation() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION '% is append-only', TG_TABLE_NAME; END $$"""
        )
        for table in AUDIT_TABLES:
            op.execute(
                f"CREATE TRIGGER {table}_no_update_delete BEFORE UPDATE OR DELETE ON {table} "
                "FOR EACH ROW EXECUTE FUNCTION reject_audit_mutation()"
            )
            op.execute(
                f"CREATE TRIGGER {table}_no_truncate BEFORE TRUNCATE ON {table} "
                "FOR EACH STATEMENT EXECUTE FUNCTION reject_audit_mutation()"
            )
    elif bind.dialect.name == "sqlite":
        op.execute(
            """CREATE TRIGGER ledger_events_cash_nonnegative BEFORE INSERT ON ledger_events
            WHEN (SELECT COALESCE(SUM(cash_delta), 0) FROM ledger_events
                  WHERE agent_id = NEW.agent_id) + NEW.cash_delta < 0
            BEGIN SELECT RAISE(ABORT, 'cash balance must remain nonnegative'); END"""
        )
        op.execute(
            """CREATE TRIGGER ledger_events_holding_nonnegative BEFORE INSERT ON ledger_events
            WHEN NEW.symbol IS NOT NULL AND
                 (SELECT COALESCE(SUM(quantity_delta), 0) FROM ledger_events
                  WHERE agent_id = NEW.agent_id AND symbol = NEW.symbol) + NEW.quantity_delta < 0
            BEGIN SELECT RAISE(ABORT, 'holding balance must remain nonnegative'); END"""
        )
        op.execute(
            """CREATE TRIGGER agent_runs_identity BEFORE INSERT ON agent_runs
            WHEN NOT EXISTS (SELECT 1 FROM agents
                             WHERE id = NEW.agent_id AND model_id = NEW.model_id)
            BEGIN SELECT RAISE(ABORT, 'agent run identity mismatch'); END"""
        )
        op.execute(
            """CREATE TRIGGER fills_identity BEFORE INSERT ON fills
            WHEN NOT EXISTS (
                SELECT 1 FROM orders o JOIN ledger_events l ON l.id = NEW.ledger_event_id
                 WHERE o.id = NEW.order_id AND o.agent_id = NEW.agent_id
                   AND l.agent_id = NEW.agent_id AND l.order_id = NEW.order_id
                   AND l.event_type = 'FILL'
            )
            BEGIN SELECT RAISE(ABORT, 'fill accounting identity mismatch'); END"""
        )
        for table in AUDIT_TABLES:
            op.execute(
                f"CREATE TRIGGER {table}_no_update BEFORE UPDATE ON {table} "
                f"BEGIN SELECT RAISE(ABORT, '{table} is append-only'); END"
            )
            op.execute(
                f"CREATE TRIGGER {table}_no_delete BEFORE DELETE ON {table} "
                f"BEGIN SELECT RAISE(ABORT, '{table} is append-only'); END"
            )


def downgrade():
    bind = op.get_bind()
    if bind.dialect.name == "postgresql":
        op.execute("DROP FUNCTION IF EXISTS reject_audit_mutation() CASCADE")
        op.execute("DROP FUNCTION IF EXISTS enforce_ledger_nonnegative() CASCADE")
        op.execute("DROP FUNCTION IF EXISTS enforce_audit_identity() CASCADE")
    op.drop_index("ix_order_rejections_decision_id", table_name="order_rejections")
    op.drop_index("ix_order_rejections_order_id", table_name="order_rejections")
    op.drop_table("order_rejections")
    op.drop_index("ix_fills_order_id", table_name="fills")
    op.drop_index("ix_fills_agent_id", table_name="fills")
    op.drop_table("fills")
    op.drop_index("ix_ledger_events_order_id", table_name="ledger_events")
    op.drop_index("ix_ledger_events_event_type", table_name="ledger_events")
    op.drop_index("ix_ledger_events_agent_id", table_name="ledger_events")
    op.drop_table("ledger_events")
    op.drop_index("ix_orders_agent_id", table_name="orders")
    op.drop_table("orders")
    op.drop_index("ix_agent_runs_agent_id", table_name="agent_runs")
    op.drop_table("agent_runs")
    op.drop_table("system_state")
    op.drop_table("agents")
