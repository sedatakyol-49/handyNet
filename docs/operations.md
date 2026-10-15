# Operations

## Database roles

| Role | Used by | Rights |
|---|---|---|
| owner (`postgres` locally, `handynet_owner` in Compose) | migrations only | owns all objects, DDL |
| `handynet_app` | the API | `SELECT/INSERT/UPDATE` on app tables, no `DELETE` (except module toggles), no `UPDATE` on audit log / stock movements, `EXECUTE` on reservation functions; RLS enforced; not superuser, no `BYPASSRLS` |

The migration creates `handynet_app` as `NOLOGIN` if it does not exist. Give it a login per environment:

```sql
ALTER ROLE handynet_app LOGIN PASSWORD '<secret>';
GRANT CONNECT ON DATABASE handynet TO handynet_app;
```

## Secrets

Never in git. Locally: `dotnet user-secrets` (project `HandyNet.Api`). Containers: environment variables from a
`.env` file that is git-ignored (template: `.env.example`) or the platform's secret store.

| Key | Purpose |
|---|---|
| `ConnectionStrings:HandyNet` | API connection as `handynet_app` |
| `ConnectionStrings:HandyNetMigrations` | owner connection for `migrate` |
| `Jwt:SigningKey` | ≥ 32 random bytes |
| `Bootstrap:AdminEmail` / `Bootstrap:AdminPassword` | creates/promotes the platform admin at startup (idempotent) |

## Deploying a new version

1. Take a backup (`deploy/backup/backup.sh`).
2. Run migrations: `docker compose run --rm migrator` (or `dotnet HandyNet.Api.dll migrate`).
3. Roll out the API. Migrations must be backward compatible with the previous API version (expand/contract).

## Backups and restore

- Baseline: nightly `pg_dump -Fc` with archive verification and 14-day retention (`deploy/backup/backup.sh`),
  copied off-site (Hetzner Storage Box / Object Storage).
- Point-in-time recovery: enable WAL archiving with pgBackRest or WAL-G to object storage (planned for the
  Hetzner production setup; not configured in Compose).
- Restore drill weekly: `deploy/backup/restore-test.sh` restores the newest dump into a scratch database and
  runs sanity queries. A failed drill is an incident.

## Health and telemetry

- `GET /health/live` (process), `GET /health/ready` (database).
- Structured logs via Serilog (console). Traces via OpenTelemetry; set `OTEL_EXPORTER_OTLP_ENDPOINT` to export.

## Personal data (GDPR)

`ankauf.intakes.seller` holds sellers' names, addresses, dates of birth and ID document numbers, collected for the
second-hand trade record-keeping duty. It is tenant-isolated (RLS) and never exposed to other organizations. A retention
job that deletes or anonymizes this data after the legally required period is **not implemented yet** — the period
must be confirmed by legal review before go-live.
