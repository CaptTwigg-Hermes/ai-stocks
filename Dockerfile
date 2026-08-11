# syntax=docker/dockerfile:1.7

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble@sha256:72dd743782f2ae7e5476fd64f6a460045e3998dc862218b80e6944cba79a01b0 AS build
WORKDIR /src
COPY AiStocks.slnx Directory.Build.props ./
COPY src ./src
COPY tests ./tests
COPY docs/nasdaq-trading-hours.html docs/nasdaq-holiday-schedule-2026.xlsx ./docs/
RUN dotnet restore AiStocks.slnx --locked-mode
RUN dotnet publish src/AiStocks.Api/AiStocks.Api.csproj -c Release --no-restore -o /out/api \
 && dotnet publish src/AiStocks.Ui/AiStocks.Ui.csproj -c Release --no-restore -o /out/ui \
 && dotnet publish src/AiStocks.Web/AiStocks.Web.csproj -c Release --no-restore -o /out/web \
 && dotnet publish src/AiStocks.Worker/AiStocks.Worker.csproj -c Release --no-restore -o /out/worker \
 && dotnet publish src/AiStocks.Collector/AiStocks.Collector.csproj -c Release --no-restore -o /out/collector \
 && dotnet publish src/AiStocks.Operations/AiStocks.Operations.csproj -c Release --no-restore -o /out/operations

FROM ghcr.io/astral-sh/uv:0.12.3@sha256:2d890623d310b57771ce840f0da5eed5fc6d657da05ffaa45d82797b53fa3abc AS uv
FROM python:3.13.5-slim-bookworm@sha256:4c2cf9917bd1cbacc5e9b07320025bdb7cdf2df7b0ceaccb55e9dd7e30987419 AS hermes-builder
ARG HERMES_COMMIT=226b095a59df0be88e195a90fbd209f236665b7b
COPY --from=uv /uv /uvx /bin/
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates git \
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

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble@sha256:f1126d438ccc359f51cc6d4701a8deae513856cf10f5fe645d29ea6403dcac6b AS runtime
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates curl libicu74 \
 && rm -rf /var/lib/apt/lists/* \
 && groupadd --system --gid 10001 aistocks \
 && useradd --system --uid 10001 --gid aistocks --home-dir /app aistocks
WORKDIR /app
ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    DOTNET_EnableDiagnostics=0 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1
USER aistocks

FROM runtime AS api
COPY --from=build --chown=aistocks:aistocks /out/api ./
EXPOSE 8080
ENTRYPOINT ["dotnet", "AiStocks.Api.dll"]

FROM runtime AS ui
COPY --from=build --chown=aistocks:aistocks /out/ui ./
EXPOSE 8080
ENTRYPOINT ["dotnet", "AiStocks.Ui.dll"]

FROM runtime AS app
COPY --from=build --chown=aistocks:aistocks /out/web ./
EXPOSE 8080
ENTRYPOINT ["dotnet", "AiStocks.Web.dll"]

FROM runtime AS collector
COPY --from=build --chown=aistocks:aistocks /out/collector ./
EXPOSE 8080
ENTRYPOINT ["dotnet", "AiStocks.Collector.dll"]

FROM runtime AS operations
COPY --from=build --chown=aistocks:aistocks /out/operations ./
ENTRYPOINT ["dotnet", "AiStocks.Operations.dll"]

FROM postgres:18.4-bookworm@sha256:882236b897e39051d2368c5ccc6cda944904723506b2dfc97f2a8f5bc9afa382 AS backup-operations
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates curl openssl \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /ops
COPY scripts/backup.sh scripts/restore-test.sh scripts/backup-cycle.sh ./scripts/
COPY src/AiStocks.Persistence/Migrations ./src/AiStocks.Persistence/Migrations
RUN chmod 0555 scripts/*.sh
ENTRYPOINT ["/ops/scripts/backup-cycle.sh"]

FROM python:3.13.5-slim-bookworm@sha256:4c2cf9917bd1cbacc5e9b07320025bdb7cdf2df7b0ceaccb55e9dd7e30987419 AS worker
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates curl libicu72 \
 && rm -rf /var/lib/apt/lists/* \
 && groupadd --system --gid 10001 aistocks \
 && useradd --system --uid 10001 --gid aistocks --home-dir /app aistocks
COPY --from=runtime /usr/share/dotnet /usr/share/dotnet
RUN ln -s /usr/share/dotnet/dotnet /usr/bin/dotnet
COPY --from=hermes-builder --chown=aistocks:aistocks /opt/hermes /opt/hermes
COPY --from=build --chown=aistocks:aistocks /out/worker /app
WORKDIR /app
ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    DOTNET_EnableDiagnostics=0 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    HERMES_EXECUTABLE=/opt/hermes/bin/hermes
USER aistocks
EXPOSE 8080
ENTRYPOINT ["dotnet", "AiStocks.Worker.dll"]

FROM worker AS reporter
COPY --from=build --chown=aistocks:aistocks /out/operations /app
ENTRYPOINT ["dotnet", "AiStocks.Operations.dll", "runtime"]
