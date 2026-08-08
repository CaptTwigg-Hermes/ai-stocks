"""${message}
Revision ID: ${up_revision}
Revises: ${down_revision | comma,n}
"""
from typing import Sequence,Union
from alembic import op
import sqlalchemy as sa
${imports if imports else ""}
revision: str = ${repr(up_revision)}
down_revision: Union[str, Sequence[str], None] = ${repr(down_revision)}
branch_labels=None
depends_on=None
def upgrade():
    ${upgrades if upgrades else "pass"}
def downgrade():
    ${downgrades if downgrades else "pass"}
