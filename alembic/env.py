import os
from logging.config import fileConfig

from sqlalchemy import engine_from_config, pool

from ai_stocks import models  # noqa: F401
from ai_stocks.db import Base
from alembic import context

config = context.config
if config.config_file_name:
    fileConfig(config.config_file_name)
url = os.getenv("DATABASE_URL", config.get_main_option("sqlalchemy.url"))
target_metadata = Base.metadata


def offline():
    context.configure(
        url=url, target_metadata=target_metadata, literal_binds=True, compare_type=True
    )
    with context.begin_transaction():
        context.run_migrations()


def online():
    cfg = config.get_section(config.config_ini_section)
    cfg["sqlalchemy.url"] = url
    connectable = engine_from_config(cfg, prefix="sqlalchemy.", poolclass=pool.NullPool)
    with connectable.connect() as connection:
        context.configure(connection=connection, target_metadata=target_metadata, compare_type=True)
        with context.begin_transaction():
            context.run_migrations()


if context.is_offline_mode():
    offline()
else:
    online()
