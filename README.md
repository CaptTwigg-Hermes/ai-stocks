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
PostgreSQL 18/PostGIS instance with separate production and test
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
docker compose build --pull app
docker compose --profile operations run --rm migrate
docker compose up -d app worker collector
docker compose ps
docker compose logs --tail=100 app worker collector
```

The migration service runs production preflight first, applies
Alembic, then idempotently bootstraps the fixed four agents and
SEK 30,000 ledgers. A preflight failure must stop before migration
or bootstrap effects.

Verify separately:

1. all three runtime services are healthy;
2. unauthenticated direct-origin requests fail closed;
3. an authorized Access session reaches the dashboard;
4. only `mike@familien-jahn.dk` can use contest controls;
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
