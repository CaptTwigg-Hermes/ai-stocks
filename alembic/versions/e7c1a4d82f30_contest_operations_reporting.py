"""contest operations, alerts, and report delivery audit

Revision ID: e7c1a4d82f30
Revises: d7b9e31c24a1
"""

from collections.abc import Sequence

import sqlalchemy as sa

from alembic import op

revision: str = "e7c1a4d82f30"
down_revision: str | Sequence[str] | None = "d7b9e31c24a1"
branch_labels = None
depends_on = None

APPEND_ONLY = (
    "contest_state_events",
    "critical_alerts",
    "daily_reports",
    "delivery_attempts",
)


def upgrade():
    with op.batch_alter_table("system_state") as batch:
        batch.add_column(
            sa.Column(
                "contest_status",
                sa.String(20),
                nullable=False,
                server_default="DRAFT",
            )
        )
        batch.add_column(sa.Column("started_at", sa.DateTime(timezone=True), nullable=True))
        batch.add_column(sa.Column("finished_at", sa.DateTime(timezone=True), nullable=True))
        batch.create_check_constraint(
            "ck_system_state_contest_status",
            "contest_status IN ('DRAFT', 'RUNNING', 'PAUSED', 'FINISHED')",
        )

    op.create_table(
        "contest_state_events",
        sa.Column("id", sa.String(40), primary_key=True),
        sa.Column("from_status", sa.String(20), nullable=False),
        sa.Column("to_status", sa.String(20), nullable=False),
        sa.Column("reason", sa.String(500), nullable=False),
        sa.Column("actor_email", sa.String(254), nullable=True),
        sa.Column("trigger_type", sa.String(30), nullable=False),
        sa.Column("occurred_at", sa.DateTime(timezone=True), nullable=False),
        sa.Column("idempotency_key", sa.String(200), nullable=False, unique=True),
        sa.CheckConstraint(
            "from_status IN ('DRAFT', 'RUNNING', 'PAUSED', 'FINISHED')",
            name="ck_contest_state_events_from_status",
        ),
        sa.CheckConstraint(
            "to_status IN ('DRAFT', 'RUNNING', 'PAUSED', 'FINISHED')",
            name="ck_contest_state_events_to_status",
        ),
        sa.CheckConstraint(
            "trigger_type IN ('OWNER', 'BAD_DATA', 'FINALIZATION')",
            name="ck_contest_state_events_trigger",
        ),
    )
    op.create_table(
        "critical_alerts",
        sa.Column("id", sa.String(40), primary_key=True),
        sa.Column("kind", sa.String(40), nullable=False, index=True),
        sa.Column("detail", sa.String(1000), nullable=False),
        sa.Column("created_at", sa.DateTime(timezone=True), nullable=False),
        sa.Column("idempotency_key", sa.String(200), nullable=False, unique=True),
        sa.CheckConstraint(
            "kind IN ('SYSTEM_PAUSE', 'INVALID_MARKET_DATA', 'DATABASE_OR_BACKUP', "
            "'MULTI_MODEL_AUTH_OUTAGE', 'ACCOUNTING_INVARIANT')",
            name="ck_critical_alert_kind",
        ),
    )
    op.create_table(
        "daily_reports",
        sa.Column("id", sa.String(40), primary_key=True),
        sa.Column("report_key", sa.String(100), nullable=False, unique=True, index=True),
        sa.Column("trading_day", sa.String(10), nullable=False, index=True),
        sa.Column("generated_at", sa.DateTime(timezone=True), nullable=False),
        sa.Column("content_hash", sa.String(64), nullable=False),
        sa.Column("message", sa.String(6000), nullable=False),
        sa.Column("payload_json", sa.JSON(), nullable=False),
        sa.CheckConstraint("length(content_hash) = 64", name="ck_daily_reports_hash"),
    )
    op.create_table(
        "delivery_attempts",
        sa.Column("id", sa.String(40), primary_key=True),
        sa.Column("reference_type", sa.String(20), nullable=False, index=True),
        sa.Column("reference_id", sa.String(40), nullable=False, index=True),
        sa.Column("attempt", sa.Integer(), nullable=False),
        sa.Column("status", sa.String(20), nullable=False),
        sa.Column("attempted_at", sa.DateTime(timezone=True), nullable=False),
        sa.Column("error", sa.String(1000), nullable=True),
        sa.Column("receipt_json", sa.JSON(), nullable=True),
        sa.CheckConstraint(
            "reference_type IN ('REPORT', 'ALERT')", name="ck_delivery_reference_type"
        ),
        sa.CheckConstraint("status IN ('SUCCESS', 'ERROR')", name="ck_delivery_attempt_status"),
        sa.CheckConstraint("attempt > 0", name="ck_delivery_attempt_positive"),
        sa.UniqueConstraint("reference_type", "reference_id", "attempt"),
    )

    bind = op.get_bind()
    if bind.dialect.name == "postgresql":
        # The initial shared trigger function referenced table-specific NEW
        # fields inside boolean expressions. PostgreSQL resolves record fields
        # before short-circuiting, so inserts into fills could try NEW.model_id.
        # Replace it with table-specific branches before production writes.
        op.execute(
            """CREATE OR REPLACE FUNCTION enforce_audit_identity()
            RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF TG_TABLE_NAME = 'agent_runs' THEN
                    IF NOT EXISTS (
                        SELECT 1 FROM agents
                         WHERE id = NEW.agent_id AND model_id = NEW.model_id
                    ) THEN
                        RAISE EXCEPTION 'agent run identity mismatch';
                    END IF;
                ELSIF TG_TABLE_NAME = 'fills' THEN
                    IF NOT EXISTS (
                        SELECT 1 FROM orders o
                        JOIN ledger_events l ON l.id = NEW.ledger_event_id
                         WHERE o.id = NEW.order_id AND o.agent_id = NEW.agent_id
                           AND l.agent_id = NEW.agent_id AND l.order_id = NEW.order_id
                           AND l.event_type = 'FILL'
                    ) THEN
                        RAISE EXCEPTION 'fill accounting identity mismatch';
                    END IF;
                END IF;
                RETURN NEW;
            END $$"""
        )
        for table in APPEND_ONLY:
            op.execute(
                f"CREATE TRIGGER {table}_no_update_delete BEFORE UPDATE OR DELETE ON {table} "
                "FOR EACH ROW EXECUTE FUNCTION reject_audit_mutation()"
            )
            op.execute(
                f"CREATE TRIGGER {table}_no_truncate BEFORE TRUNCATE ON {table} "
                "FOR EACH STATEMENT EXECUTE FUNCTION reject_audit_mutation()"
            )
    elif bind.dialect.name == "sqlite":
        for table in APPEND_ONLY:
            op.execute(
                f"CREATE TRIGGER {table}_no_update BEFORE UPDATE ON {table} "
                f"BEGIN SELECT RAISE(ABORT, '{table} is append-only'); END"
            )
            op.execute(
                f"CREATE TRIGGER {table}_no_delete BEFORE DELETE ON {table} "
                f"BEGIN SELECT RAISE(ABORT, '{table} is append-only'); END"
            )


def downgrade():
    for table in reversed(APPEND_ONLY):
        op.drop_table(table)
    with op.batch_alter_table("system_state") as batch:
        batch.drop_constraint("ck_system_state_contest_status", type_="check")
        batch.drop_column("finished_at")
        batch.drop_column("started_at")
        batch.drop_column("contest_status")
