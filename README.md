# AI Stocks

Private Nasdaq Stockholm paper-trading competition. It has no
broker integration and cannot place real orders.

## Production topology

- Dockge runs `app`, `worker`, and `collector`.
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

Do not start the contest until every release gate in
`docs/acceptance-contract.md` has passed.

## Dockge preparation

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
AUD, or Hermes credentials.

Create the three host datasets from `.env` before opening the stack
in Dockge:

```bash
mkdir -p "$HERMES_AUTH_DIR" "$NASDAQ_ARCHIVE_DIR" "$NASDAQ_STATUS_DIR"
chown -R 10001:10001 "$HERMES_AUTH_DIR" "$NASDAQ_ARCHIVE_DIR" "$NASDAQ_STATUS_DIR"
chmod 700 "$HERMES_AUTH_DIR" "$NASDAQ_ARCHIVE_DIR" "$NASDAQ_STATUS_DIR"
```

`HERMES_AUTH_DIR` must contain only the credentials/configuration
needed by the isolated Hermes runner. Archive and status datasets
must be writable by UID/GID 10001. The app and worker mount market
data read-only.

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
docker compose config --quiet
docker compose build --pull app worker collector backup-scheduler
docker compose --profile operations run --rm migrate
docker compose --profile operations run --rm migrate bootstrap
docker compose up -d collector
# Wait for /readyz, then run the separately deployed archive-to-PostgreSQL importer.
# Do not continue until every eligible instrument has 20 complete verified sessions.
docker compose --profile operations run --rm migrate preflight
docker compose up -d app worker
docker compose --profile operations up -d backup-scheduler
docker compose ps
docker compose logs --tail=100 app worker collector
```

The migration runner applies checksum-locked SQL migrations. Those
migrations idempotently create the fixed four agents and SEK 30,000
ledgers. The explicit bootstrap command verifies that exact state;
the final preflight command must pass before app/worker start. The collector
must start first so `/readyz` can prove the filesystem feed is complete;
preflight comes only after the separate PostgreSQL importer has loaded the
required history. This repository does not yet compose that importer, so a
clean deployment must stop here rather than claim launch readiness.

Verify separately:

1. all three runtime services are healthy;
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
docker compose stop app worker collector
```

Do not use `docker compose down --volumes` as an operational habit.
On bad data, accounting failure, database/backup failure, or model
authentication outage: pause the contest, preserve logs, rotate
affected credentials, and restore only from a verified backup.
