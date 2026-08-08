# syntax=docker/dockerfile:1.7
FROM ghcr.io/astral-sh/uv:0.8.4@sha256:40775a79214294fb51d097c9117592f193bcfdfc634f4daa0e169ee965b10ef0 AS uv

FROM python:3.13.5-slim-bookworm@sha256:4c2cf9917bd1cbacc5e9b07320025bdb7cdf2df7b0ceaccb55e9dd7e30987419 AS base

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    UV_COMPILE_BYTECODE=1 \
    UV_LINK_MODE=copy \
    PATH="/app/.venv/bin:$PATH"

RUN groupadd --system --gid 10001 app \
    && useradd --system --uid 10001 --gid app --home-dir /app app

WORKDIR /app
COPY --from=uv /uv /uvx /bin/
COPY pyproject.toml uv.lock ./
RUN uv sync --frozen --no-dev --no-install-project

COPY --chown=app:app ai_stocks ./ai_stocks
COPY --chown=app:app config ./config
COPY --chown=app:app alembic ./alembic
COPY --chown=app:app alembic.ini ./alembic.ini
COPY --chown=app:app scripts/entrypoint.sh ./entrypoint.sh
COPY --chown=app:app scripts/migrate.sh ./migrate.sh
RUN chmod 0555 /app/entrypoint.sh /app/migrate.sh

FROM base AS app
USER app
EXPOSE 8000
CMD ["./entrypoint.sh"]

FROM base AS hermes-builder
ARG HERMES_COMMIT=226b095a59df0be88e195a90fbd209f236665b7b
RUN apt-get update \
    && apt-get install -y --no-install-recommends git ca-certificates \
    && rm -rf /var/lib/apt/lists/* \
    && git init /opt/hermes \
    && git -C /opt/hermes remote add origin https://github.com/NousResearch/hermes-agent.git \
    && git -C /opt/hermes fetch --depth 1 origin "$HERMES_COMMIT" \
    && git -C /opt/hermes checkout --detach "$HERMES_COMMIT" \
    && uv sync --frozen --no-dev --extra cli --extra web --project /opt/hermes --python /usr/local/bin/python \
    && mkdir -p /opt/hermes/bin \
    && ln -s /opt/hermes/.venv/bin/hermes /opt/hermes/bin/hermes \
    && test "$(git -C /opt/hermes rev-parse HEAD)" = "$HERMES_COMMIT" \
    && /opt/hermes/bin/hermes --help >/dev/null \
    && rm -rf /opt/hermes/.git

FROM base AS worker
COPY --from=hermes-builder --chown=app:app /opt/hermes /opt/hermes
RUN /opt/hermes/bin/hermes --help >/dev/null
USER app
CMD ["python", "-m", "ai_stocks.worker_cli"]
