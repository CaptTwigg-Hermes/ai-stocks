"""lifecycle audit, corporate actions, and final rankings

Revision ID: d7b9e31c24a1
Revises: c7d4a8f21b09
"""

from collections.abc import Sequence

import sqlalchemy as sa

from alembic import op

revision: str = "d7b9e31c24a1"
down_revision: str | Sequence[str] | None = "c7d4a8f21b09"
branch_labels = None
depends_on = None

TABLES = ("order_lifecycle_events", "corporate_actions", "final_rankings")


def upgrade():
    op.create_table(
        "order_lifecycle_events",
        sa.Column("id", sa.String(40), nullable=False),
        sa.Column("order_id", sa.String(40), nullable=False),
        sa.Column("agent_id", sa.String(40), nullable=False),
        sa.Column("event_type", sa.String(20), nullable=False),
        sa.Column("reason", sa.String(4000), nullable=False),
        sa.Column("occurred_at", sa.DateTime(timezone=True), nullable=False),
        sa.Column("request_id", sa.String(100), nullable=False),
        sa.Column("request_hash", sa.String(64), nullable=False),
        sa.Column("replacement_order_id", sa.String(40), nullable=True),
        sa.CheckConstraint(
            "event_type IN ('CANCELLED', 'REPLACED')", name="ck_order_lifecycle_type"
        ),
        sa.CheckConstraint("length(request_hash) = 64", name="ck_order_lifecycle_hash"),
        sa.ForeignKeyConstraint(["agent_id"], ["agents.id"]),
        sa.ForeignKeyConstraint(["order_id"], ["orders.id"]),
        sa.ForeignKeyConstraint(["replacement_order_id"], ["orders.id"]),
        sa.PrimaryKeyConstraint("id"),
        sa.UniqueConstraint("agent_id", "request_id"),
    )
    op.create_index("ix_order_lifecycle_events_order_id", "order_lifecycle_events", ["order_id"])
    op.create_index("ix_order_lifecycle_events_agent_id", "order_lifecycle_events", ["agent_id"])
    op.create_table(
        "corporate_actions",
        sa.Column("id", sa.String(40), nullable=False),
        sa.Column("agent_id", sa.String(40), nullable=False),
        sa.Column("reference", sa.String(100), nullable=False),
        sa.Column("action_type", sa.String(30), nullable=False),
        sa.Column("symbol", sa.String(30), nullable=False),
        sa.Column("effective_at", sa.DateTime(timezone=True), nullable=False),
        sa.Column("payload_json", sa.JSON(), nullable=False),
        sa.CheckConstraint(
            "action_type IN ('DIVIDEND', 'SPLIT', 'CASH_MERGER', 'STOCK_MERGER', 'DELISTING')",
            name="ck_corporate_action_type",
        ),
        sa.ForeignKeyConstraint(["agent_id"], ["agents.id"]),
        sa.PrimaryKeyConstraint("id"),
        sa.UniqueConstraint("agent_id", "reference"),
    )
    op.create_index("ix_corporate_actions_agent_id", "corporate_actions", ["agent_id"])
    op.create_index("ix_corporate_actions_action_type", "corporate_actions", ["action_type"])
    op.create_index("ix_corporate_actions_symbol", "corporate_actions", ["symbol"])
    op.create_table(
        "final_rankings",
        sa.Column("id", sa.String(40), nullable=False),
        sa.Column("reference", sa.String(100), nullable=False),
        sa.Column("agent_id", sa.String(40), nullable=False),
        sa.Column("rank", sa.Integer(), nullable=False),
        sa.Column("net_liquidation_value", sa.Numeric(18, 2), nullable=False),
        sa.Column("finalized_at", sa.DateTime(timezone=True), nullable=False),
        sa.Column("input_hash", sa.String(64), nullable=False),
        sa.Column("liquidation_json", sa.JSON(), nullable=False),
        sa.CheckConstraint("rank > 0", name="ck_final_rank_positive"),
        sa.CheckConstraint("net_liquidation_value >= 0", name="ck_final_value_nonnegative"),
        sa.CheckConstraint("length(input_hash) = 64", name="ck_final_input_hash"),
        sa.ForeignKeyConstraint(["agent_id"], ["agents.id"]),
        sa.PrimaryKeyConstraint("id"),
        sa.UniqueConstraint("reference", "agent_id"),
        sa.UniqueConstraint("reference", "rank"),
    )
    op.create_index("ix_final_rankings_reference", "final_rankings", ["reference"])
    op.create_index("ix_final_rankings_agent_id", "final_rankings", ["agent_id"])

    bind = op.get_bind()
    if bind.dialect.name == "postgresql":
        for table in TABLES:
            op.execute(
                f"CREATE TRIGGER {table}_no_update_delete BEFORE UPDATE OR DELETE ON {table} "
                "FOR EACH ROW EXECUTE FUNCTION reject_audit_mutation()"
            )
            op.execute(
                f"CREATE TRIGGER {table}_no_truncate BEFORE TRUNCATE ON {table} "
                "FOR EACH STATEMENT EXECUTE FUNCTION reject_audit_mutation()"
            )
    elif bind.dialect.name == "sqlite":
        for table in TABLES:
            op.execute(
                f"CREATE TRIGGER {table}_no_update BEFORE UPDATE ON {table} "
                f"BEGIN SELECT RAISE(ABORT, '{table} is append-only'); END"
            )
            op.execute(
                f"CREATE TRIGGER {table}_no_delete BEFORE DELETE ON {table} "
                f"BEGIN SELECT RAISE(ABORT, '{table} is append-only'); END"
            )


def downgrade():
    op.drop_index("ix_final_rankings_agent_id", table_name="final_rankings")
    op.drop_index("ix_final_rankings_reference", table_name="final_rankings")
    op.drop_table("final_rankings")
    op.drop_index("ix_corporate_actions_symbol", table_name="corporate_actions")
    op.drop_index("ix_corporate_actions_action_type", table_name="corporate_actions")
    op.drop_index("ix_corporate_actions_agent_id", table_name="corporate_actions")
    op.drop_table("corporate_actions")
    op.drop_index("ix_order_lifecycle_events_agent_id", table_name="order_lifecycle_events")
    op.drop_index("ix_order_lifecycle_events_order_id", table_name="order_lifecycle_events")
    op.drop_table("order_lifecycle_events")
