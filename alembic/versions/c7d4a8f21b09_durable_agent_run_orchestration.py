"""durable scheduled agent-run orchestration

Revision ID: c7d4a8f21b09
Revises: ab3aa6da9692
"""

from collections.abc import Sequence

import sqlalchemy as sa

from alembic import op

revision: str = "c7d4a8f21b09"
down_revision: str | Sequence[str] | None = "ab3aa6da9692"
branch_labels = None
depends_on = None

_MODEL_CHECK = (
    "model_id IN ('gpt-5.6-sol', 'claude-opus-4.8', 'claude-sonnet-5', 'gemini-3.1-pro-preview')"
)


def upgrade():
    op.create_table(
        "scheduled_agent_runs",
        sa.Column("run_key", sa.String(length=100), nullable=False),
        sa.Column("agent_id", sa.String(length=40), nullable=False),
        sa.Column("model_id", sa.String(length=100), nullable=False),
        sa.Column("scheduled_at", sa.DateTime(timezone=True), nullable=False),
        sa.Column("deadline_at", sa.DateTime(timezone=True), nullable=False),
        sa.Column("status", sa.String(length=20), nullable=False),
        sa.Column("attempt_count", sa.Integer(), nullable=False),
        sa.Column("next_attempt_at", sa.DateTime(timezone=True), nullable=False),
        sa.Column("claim_token", sa.String(length=40), nullable=True),
        sa.Column("lease_until", sa.DateTime(timezone=True), nullable=True),
        sa.CheckConstraint(_MODEL_CHECK, name="ck_scheduled_agent_runs_model_id"),
        sa.CheckConstraint(
            "status IN ('PENDING', 'CLAIMED', 'COMPLETED', 'MISSED')",
            name="ck_scheduled_agent_runs_status",
        ),
        sa.CheckConstraint("deadline_at >= scheduled_at", name="ck_scheduled_agent_runs_window"),
        sa.CheckConstraint("attempt_count >= 0", name="ck_scheduled_agent_runs_attempts"),
        sa.ForeignKeyConstraint(["agent_id"], ["agents.id"]),
        sa.PrimaryKeyConstraint("run_key"),
    )
    op.create_index("ix_scheduled_agent_runs_agent_id", "scheduled_agent_runs", ["agent_id"])
    op.create_index(
        "ix_scheduled_agent_runs_scheduled_at", "scheduled_agent_runs", ["scheduled_at"]
    )
    op.create_index("ix_scheduled_agent_runs_status", "scheduled_agent_runs", ["status"])

    # Nullable only preserves immutable pre-orchestrator audit rows during an in-place upgrade.
    # Every row emitted by AgentOrchestrator populates all new audit fields.
    with op.batch_alter_table("agent_runs") as batch:
        batch.add_column(sa.Column("run_key", sa.String(length=100), nullable=True))
        batch.add_column(sa.Column("attempt", sa.Integer(), nullable=True))
        batch.add_column(sa.Column("prompt", sa.String(), nullable=True))
        batch.add_column(sa.Column("stderr", sa.String(), nullable=True))
        batch.add_column(sa.Column("command_json", sa.JSON(), nullable=True))
        batch.add_column(sa.Column("provenance_json", sa.JSON(), nullable=True))
        batch.add_column(sa.Column("decision_json", sa.JSON(), nullable=True))
        batch.add_column(sa.Column("validation_error", sa.String(), nullable=True))
        batch.add_column(sa.Column("retry_at", sa.DateTime(timezone=True), nullable=True))
        batch.add_column(sa.Column("missed_reason", sa.String(length=200), nullable=True))
        batch.create_foreign_key(
            "fk_agent_runs_run_key", "scheduled_agent_runs", ["run_key"], ["run_key"]
        )
        batch.create_unique_constraint("uq_agent_runs_run_attempt", ["run_key", "attempt"])
        batch.create_index("ix_agent_runs_run_key", ["run_key"])

    if op.get_bind().dialect.name == "sqlite":
        _create_sqlite_agent_run_triggers()


def downgrade():
    if op.get_bind().dialect.name == "sqlite":
        op.execute("DROP TRIGGER IF EXISTS agent_runs_identity")
        op.execute("DROP TRIGGER IF EXISTS agent_runs_no_update")
        op.execute("DROP TRIGGER IF EXISTS agent_runs_no_delete")
    with op.batch_alter_table("agent_runs") as batch:
        batch.drop_index("ix_agent_runs_run_key")
        batch.drop_constraint("uq_agent_runs_run_attempt", type_="unique")
        batch.drop_constraint("fk_agent_runs_run_key", type_="foreignkey")
        for column in (
            "missed_reason",
            "retry_at",
            "validation_error",
            "decision_json",
            "provenance_json",
            "command_json",
            "stderr",
            "prompt",
            "attempt",
            "run_key",
        ):
            batch.drop_column(column)
    if op.get_bind().dialect.name == "sqlite":
        _create_sqlite_agent_run_triggers()
    op.drop_index("ix_scheduled_agent_runs_status", table_name="scheduled_agent_runs")
    op.drop_index("ix_scheduled_agent_runs_scheduled_at", table_name="scheduled_agent_runs")
    op.drop_index("ix_scheduled_agent_runs_agent_id", table_name="scheduled_agent_runs")
    op.drop_table("scheduled_agent_runs")


def _create_sqlite_agent_run_triggers():
    op.execute(
        """CREATE TRIGGER IF NOT EXISTS agent_runs_identity BEFORE INSERT ON agent_runs
        WHEN NOT EXISTS (SELECT 1 FROM agents
                         WHERE id = NEW.agent_id AND model_id = NEW.model_id)
        BEGIN SELECT RAISE(ABORT, 'agent run identity mismatch'); END"""
    )
    op.execute(
        """CREATE TRIGGER IF NOT EXISTS agent_runs_no_update BEFORE UPDATE ON agent_runs
        BEGIN SELECT RAISE(ABORT, 'agent_runs is append-only'); END"""
    )
    op.execute(
        """CREATE TRIGGER IF NOT EXISTS agent_runs_no_delete BEFORE DELETE ON agent_runs
        BEGIN SELECT RAISE(ABORT, 'agent_runs is append-only'); END"""
    )
