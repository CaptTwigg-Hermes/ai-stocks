# AI Stocks

Private Nasdaq Stockholm paper-trading competition. It has no
broker integration and cannot place real orders.

## Contracts

- [`docs/acceptance-contract.md`](docs/acceptance-contract.md) and
  [`docs/market-data-contract.md`](docs/market-data-contract.md) remain the
  authoritative legacy Swedish 2026 championship contracts.
- [`docs/v2-acceptance-contract.md`](docs/v2-acceptance-contract.md) and
  [`docs/v2-market-data-provider-contract.md`](docs/v2-market-data-provider-contract.md)
  define the separate global human + AI v2 platform. V2 production execution is
  blocked until its provider approval and verification gates pass.

## Production topology

- The authoritative private 2026 contest uses the explicit `contest` profile:
  Dockge runs `app`, `worker`, `collector`, and `reporter`.
- PostgreSQL is external. There is no database service in this
  Compose stack.
- The app binds by default to `192.168.50.2:3232` for the existing
  Cloudflare Tunnel.
- Cloudflare Access protects
  `https://ai-stocks.jahn-software.com`.
- The origin validates the Access JWT signature, issuer, audience,
  time claims, and exact request origin. A direct request without a
  valid Access JWT fails closed.
- `migrate` and `backup-tools` are one-shot services under the
  `operations` profile.

The separate `api`, `ui`, least-privilege `exhibition` worker, and
`preview-collector` are an explicit **local AI exhibition** under the `preview`
profile. Four pinned Copilot models research independently and submit validated
paper-only decisions against clearly labelled, non-live delayed post-trade
observations. Each starts with 100,000 DKK. `AI_EXHIBITION_UNIVERSE=stockholm`
retains the v1 XSTO/SEK exhibition. Explicitly selecting `nordic` enables the
private v2 Nordic venue/native-currency profile with checksum-bound ECB
informational DKK reference conversions. Human search and trading routes are
not exposed in this mode. Exhibition state is durable in PostgreSQL, has no
broker capability, is not the strict contest ledger, and must not be routed
through the production Cloudflare hostname. The preview profile reuses port
3232, so never enable `contest` and `preview` together.

Nordic preview startup also requires a current reviewed corporate-action input
directory. The collector emits an append-only block state every poll; the API
fails closed if that state is absent or stale and excludes every listed
venue/ISIN/order-book identity because corporate actions are not applied by the
preview ledger.

The `warmup` profile runs only `warmup-collector`. It accumulates the strict
contest's 20 verified sessions and must run separately from both contest and
preview because all collector modes share the same immutable archive. Stop
preview before enabling warmup, and stop warmup before enabling preview or
contest.

Do not start the legacy contest until every release gate in
[`docs/acceptance-contract.md`](docs/acceptance-contract.md) has passed. Its gate
does not approve v2, and a v2 seed/test-provider demonstration does not approve
either production deployment.

## Dockge preparation

Pushes to `main` publish nine target-specific images to
`ghcr.io/capttwigg-hermes/ai-stocks`. The Compose stack pulls
`app-latest`, `api-latest`, `ui-latest`, `exhibition-latest`,
`collector-latest`, `worker-latest`, `reporter-latest`, `operations-latest`, and
`backup-operations-latest`; Dockge no longer needs a local source checkout or
Docker build context. Set
`AISTOCKS_IMAGE_VERSION` to a full Git commit SHA instead of `latest`
when an immutable rollback target is required.

The repository and GHCR package are private. Before Dockge pulls the
stack, authenticate the Docker client used by Dockge with a classic
GitHub PAT carrying only `read:packages`:

```bash
printf '%s' "$GHCR_TOKEN" | docker login ghcr.io -u CaptTwiggHermes --password-stdin
unset GHCR_TOKEN
```

Never place that token in Compose or `.env`. If Dockge itself runs in a
container, its Docker credential directory—not merely an unrelated host
shell—must contain the successful login.

This stack requires Docker Compose, OpenSSL, and an existing
PostgreSQL 18 instance with separate production and test
roles/databases.

```bash
cp .env.example .env
chmod 600 .env
mkdir -p secrets backups
openssl rand -base64 48 > secrets/backup-passphrase
chmod 600 secrets/backup-passphrase
```

Replace every placeholder in `.env`. Never commit `.env`,
`secrets/`, backups, database credentials, the Cloudflare Access
AUD, or Hermes credentials. Export a least-privilege Copilot-only file from the
active Hermes home before starting the exhibition; the exporter creates a new
mode-0600 file and refuses to overwrite an existing destination:

```bash
python3 scripts/export-copilot-env.py /opt/data/.env "$HERMES_COPILOT_ENV_FILE"
```

Create the host datasets from `.env` before opening the stack
in Dockge:

```bash
mkdir -p "$HERMES_AUTH_DIR" "$NASDAQ_ARCHIVE_DIR" "$NASDAQ_STATUS_DIR" "$MARKET_BOOTSTRAP_DIR" "$CORPORATE_ACTION_INPUT_DIR"
chown -R 10001:10001 "$HERMES_AUTH_DIR" "$NASDAQ_ARCHIVE_DIR" "$NASDAQ_STATUS_DIR" "$CORPORATE_ACTION_INPUT_DIR"
chmod 700 "$HERMES_AUTH_DIR" "$NASDAQ_ARCHIVE_DIR" "$NASDAQ_STATUS_DIR" "$CORPORATE_ACTION_INPUT_DIR"
```

`HERMES_COPILOT_ENV_FILE` must be a mode-0600 file containing only
`COPILOT_GITHUB_TOKEN`. The exhibition worker copies it
into a separate writable runtime home for each agent; never point it at the main
Hermes home. `HERMES_AUTH_DIR` remains the strict contest runner's isolated
credential directory. Archive and status datasets
must be writable by UID/GID 10001. The app and worker mount market
data read-only.

The reporter requires `OPERATIONS_DATABASE_URL`, `HERMES_AUTH_DIR`,
`DISCORD_REPORT_TARGET` (a numeric `discord:<channel>` target), and optionally
`OPERATIONS_POLL_SECONDS` (default 30). It is the only Discord sender: daily
reports and the five approved immediate-alert kinds use the same PostgreSQL
lease, immutable audit, receipt, and idempotency path. Backup failures enqueue
through `BACKUP_DATABASE_URL`; no unaudited Discord webhook is used.

`CORPORATE_ACTION_INPUT_DIR` is a read-only owner-reviewed drop directory for
schema-version-1 JSON normalizations. Each input identifies the XSTO
ISIN/order-book, one of `DIVIDEND`, `SPLIT`, `CASH_MERGER`, `STOCK_MERGER`,
`DELISTING`, or `CORRECTION`, exact normalized values, Nasdaq Main Markets
evidence, an independent HTTPS evidence source, both payload SHA-256 values,
and an `owner:*` approval/time. The collector is the sole production ingester;
PostgreSQL rejects missing/mismatched evidence, conflicting replay, or direct
runtime writes and retains the exact immutable input bytes and hash.

`MARKET_BOOTSTRAP_DIR` is read-only configuration and needs only
`firds-plan.json`. The plan is an ordered, checksummed acquisition manifest;
the collector downloads every unapplied ESMA
artifact itself and applies the initial `full` followed by contiguous `delta`
entries. Example:

```json
{"artifacts":[
  {"kind":"full","sourceUrl":"https://firds.esma.europa.eu/firds/FULINS_E_20260806_01of02.zip","sha256":"<64 lowercase hex>","version":"full-2026-08-06-1","cursor":1,"effectiveAt":"2026-08-06"},
  {"kind":"full-part","sourceUrl":"https://firds.esma.europa.eu/firds/FULINS_E_20260806_02of02.zip","sha256":"<64 lowercase hex>","version":"full-2026-08-06-2","cursor":2,"effectiveAt":"2026-08-06"},
  {"kind":"delta","sourceUrl":"https://firds.esma.europa.eu/firds/DLTINS_E_20260807_01of01.zip","sha256":"<64 lowercase hex>","version":"delta-2026-08-07","cursor":3,"effectiveAt":"2026-08-07"}
]}
```

The collector also fetches Nasdaq Main Markets RSS on every poll, archives the
immutable raw XML by SHA-256, and applies suspension, observation, warning, and
resumption notices to durable status. For this private paper-only deployment,
eligible FIRDS common shares begin `Clear` unless the fetched public RSS snapshot
says otherwise. This best-effort bootstrap can miss an older active status omitted
from the finite RSS window; it is intentionally unsuitable for real-money or
regulated trading. FIRDS/RSS provenance remains transactional with trade authority.
Add reviewed delta entries to the mounted plan before their effective session. A
clean `NASDAQ_ARCHIVE_DIR` is supported; a missing or invalid plan, checksum,
cursor, RSS response, or upstream artifact keeps `/readyz` closed.

Before deployment, verify that the Dockge host owns
`192.168.50.2`, that port `3232` is free, and that the existing
Cloudflare Tunnel route targets `http://192.168.50.2:3232`.
Do not publish PostgreSQL through this stack.

## Explicit migration and bootstrap

Ordinary app startup never runs migrations. In Dockge, build the
candidate image, then run the one-shot `migrate` service from the
`operations` profile before starting runtime services.

Equivalent CLI verification:

```bash
scripts/compose-mode.sh contest config --quiet
scripts/compose-mode.sh contest pull app worker collector reporter
scripts/compose-mode.sh operations pull backup-scheduler
scripts/compose-mode.sh operations run --rm migrate
scripts/compose-mode.sh operations run --rm migrate bootstrap
scripts/compose-mode.sh contest up -d collector
# The collector acquires FIRDS/RSS/post-trade bytes and projects verified observations.
# Wait until it has accumulated 20 complete verified sessions and /readyz passes.
scripts/compose-mode.sh operations run --rm migrate preflight
scripts/compose-mode.sh contest up -d app worker reporter
scripts/compose-mode.sh operations up -d backup-scheduler
scripts/compose-mode.sh contest ps
scripts/compose-mode.sh contest logs --tail=100 app worker collector reporter
```

To run the local volatile AI exhibition at the same LAN address, stop all
contest runtimes first. The preflight refuses to start either runtime mode while
the other is active and forbids additive `--profile` arguments:

```bash
scripts/compose-mode.sh contest stop app worker collector reporter
# Set AI_EXHIBITION_UNIVERSE=nordic in Dockge/.env for the local Nordic v2 profile.
scripts/compose-mode.sh preview up -d preview-collector api ui exhibition
scripts/compose-mode.sh preview ps
```

Warm the strict market-data path without stopping the exhibition:

```bash
scripts/compose-mode.sh warmup up -d warmup-collector
scripts/compose-mode.sh warmup ps
scripts/compose-mode.sh warmup logs --tail=100 warmup-collector
```

The migration runner applies checksum-locked SQL migrations. Those
migrations idempotently create the fixed four agents and SEK 30,000
ledgers. The explicit bootstrap command verifies that exact state;
the final preflight command must pass before app/worker start. The collector
must start first: it performs clean-start reference acquisition, bounded
post-trade catch-up, immutable archival, and the transactionally bound
PostgreSQL observation projection. `/readyz` remains closed until the required
20 consecutive complete sessions have actually accumulated.

Verify separately:

1. all four contest runtime services are running and the three contest HTTP runtime services are healthy;
2. unauthenticated direct-origin requests fail closed;
3. an authorized Access session reaches the dashboard;
4. only identities in `ACCESS_OWNER_EMAILS` can use contest controls;
5. worker and collector logs contain no credentials;
6. all four exact models work from the production-built image.

## Backups

`scripts/backup.sh` runs `pg_dump` in the digest-pinned
`backup-tools` container and streams a custom-format dump through
AES-256-CBC/PBKDF2 encryption. No plaintext dump is written.
It creates one atomic backup per UTC day and defaults to 550-day
retention.

```bash
export BACKUP_DATABASE_URL="$(grep '^BACKUP_DATABASE_URL=' .env | cut -d= -f2-)"
scripts/backup.sh
```

Keep an off-host encrypted copy and a separately protected copy of
the passphrase. Monitor every backup job; a created file alone is
not proof of recoverability.

For Dockge, start `backup-scheduler` in the `operations` profile. It runs an
encrypted backup and disposable restore rehearsal every
`BACKUP_INTERVAL_SECONDS`, validates migration checksums and contest/accounting
invariants, clears `ai_stocks_test`, and posts failures to the private Discord
webhook in `BACKUP_ALERT_WEBHOOK_URL`. No Cloudflare sidecar is required.

## Destructive restore rehearsal

`RESTORE_DATABASE_URL` must resolve to exactly `ai_stocks_test`.
The restore script refuses every other database, drops and rebuilds
only that test database's `public` schema, verifies the restored
archive, and clears the schema on exit.

```bash
export RESTORE_DATABASE_URL="$(grep '^RESTORE_DATABASE_URL=' .env | cut -d= -f2-)"
scripts/restore-test.sh
# or
scripts/restore-test.sh backups/ai-stocks-2026-08-06.dump.enc
```

Never point `RESTORE_DATABASE_URL` at production. A release is not
backup-ready until a real encrypted backup and disposable restore
have both succeeded in the Docker-capable deployment environment.

## Stop and incident handling

```bash
scripts/compose-mode.sh contest stop app worker collector reporter
scripts/compose-mode.sh preview stop api ui
```

Do not use `docker compose down --volumes` as an operational habit.
On bad data, accounting failure, database/backup failure, or model
authentication outage: pause the contest, preserve logs, rotate
affected credentials, and restore only from a verified backup.
