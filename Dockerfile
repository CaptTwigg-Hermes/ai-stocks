# syntax=docker/dockerfile:1.7

FROM mcr.microsoft.com/dotnet/sdk:10.0-bookworm-slim AS build
WORKDIR /src
COPY AiStocks.slnx Directory.Build.props ./
COPY src ./src
COPY tests ./tests
RUN dotnet restore AiStocks.slnx --locked-mode
RUN dotnet publish src/AiStocks.Web/AiStocks.Web.csproj -c Release --no-restore -o /out/web \
 && dotnet publish src/AiStocks.Worker/AiStocks.Worker.csproj -c Release --no-restore -o /out/worker \
 && dotnet publish src/AiStocks.Collector/AiStocks.Collector.csproj -c Release --no-restore -o /out/collector \
 && dotnet publish src/AiStocks.Operations/AiStocks.Operations.csproj -c Release --no-restore -o /out/operations

FROM ghcr.io/astral-sh/uv:0.8.4@sha256:40775a79214294fb51d097c9117592f193bcfdfc634f4daa0e169ee965b10ef0 AS uv
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

FROM mcr.microsoft.com/dotnet/aspnet:10.0-bookworm-slim AS runtime
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates curl \
 && rm -rf /var/lib/apt/lists/* \
 && groupadd --system --gid 10001 app \
 && useradd --system --uid 10001 --gid app --home-dir /app app
WORKDIR /app
ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    DOTNET_EnableDiagnostics=0 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1
USER app

FROM runtime AS app
COPY --from=build --chown=app:app /out/web ./
EXPOSE 8080
ENTRYPOINT ["dotnet", "AiStocks.Web.dll"]

FROM runtime AS collector
COPY --from=build --chown=app:app /out/collector ./
EXPOSE 8080
ENTRYPOINT ["dotnet", "AiStocks.Collector.dll"]

FROM runtime AS operations
COPY --from=build --chown=app:app /out/operations ./
ENTRYPOINT ["dotnet", "AiStocks.Operations.dll"]

FROM python:3.13.5-slim-bookworm@sha256:4c2cf9917bd1cbacc5e9b07320025bdb7cdf2df7b0ceaccb55e9dd7e30987419 AS worker
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates curl \
 && rm -rf /var/lib/apt/lists/* \
 && groupadd --system --gid 10001 app \
 && useradd --system --uid 10001 --gid app --home-dir /app app
COPY --from=runtime /usr/share/dotnet /usr/share/dotnet
RUN ln -s /usr/share/dotnet/dotnet /usr/bin/dotnet
COPY --from=hermes-builder --chown=app:app /opt/hermes /opt/hermes
COPY --from=build --chown=app:app /out/worker /app
WORKDIR /app
ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    DOTNET_EnableDiagnostics=0 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    HERMES_EXECUTABLE=/opt/hermes/bin/hermes
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "AiStocks.Worker.dll"]
