#!/bin/sh
set -eu
python -m ai_stocks.preflight
alembic upgrade head
python -m ai_stocks.bootstrap
