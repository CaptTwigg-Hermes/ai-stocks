#!/bin/sh
set -eu
python -m ai_stocks.preflight
exec python -m uvicorn ai_stocks.app:create_app --factory --host 0.0.0.0 --port 8000 --proxy-headers --forwarded-allow-ips="${TRUSTED_PROXY_IPS:-127.0.0.1}"
